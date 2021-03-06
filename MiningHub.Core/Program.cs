﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using AutoMapper;
using FluentValidation;
using Microsoft.Extensions.CommandLineUtils;
using MiningHub.Core.Api;
using MiningHub.Core.Api.Responses;
using MiningHub.Core.Configuration;
using MiningHub.Core.Extensions;
using MiningHub.Core.Mining;
using MiningHub.Core.Native;
using MiningHub.Core.Payments;
using MiningHub.Core.Persistence.Dummy;
using MiningHub.Core.Persistence.Postgres;
using MiningHub.Core.Persistence.Postgres.Repositories;
using MiningHub.Core.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using NLog.Conditions;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace MiningHub.Core
{
    public class Program
    {
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();
        private static ILogger logger;
        private static IContainer container;
        private static CommandOption dumpConfigOption;
        private static CommandOption shareRecoveryOption;
        private static ShareRecorder shareRecorder;
        private static ShareRelay shareRelay;
        private static ShareReceiver shareReceiver;
        private static PayoutManager payoutManager;
        private static StatsRecorder statsRecorder;
        private static ClusterConfig clusterConfig;
        private static ApiServer apiServer;

        public static AdminGcStats gcStats = new AdminGcStats();

        private static readonly Regex regexJsonTypeConversionError =
            new Regex("\"([^\"]+)\"[^\']+\'([^\']+)\'.+\\s(\\d+),.+\\s(\\d+)", RegexOptions.Compiled);

        public static void Main(string[] args)
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                Console.CancelKeyPress += OnCancelKeyPress;

#if DEBUG
                PreloadNativeLibs();
#endif

                if (!HandleCommandLineOptions(args, out var configFile))
                    return;

                Logo();
                clusterConfig = ReadConfig(configFile);

                if (dumpConfigOption.HasValue())
                {
                    DumpParsedConfig(clusterConfig);
                    return;
                }

                ValidateConfig();
                Bootstrap();
                LogRuntimeInfo();

                if (!shareRecoveryOption.HasValue())
                {
                    if(!cts.IsCancellationRequested)
                        Start().Wait(cts.Token);
                }

                else
                    RecoverShares(shareRecoveryOption.Value());
            }

            catch (PoolStartupAbortException ex)
            {
                if (!string.IsNullOrEmpty(ex.Message))
                    Console.WriteLine(ex.Message);

                Console.WriteLine("\nCluster cannot start. Good Bye!");
            }

            catch (JsonException)
            {
                // ignored
            }

            catch (IOException)
            {
                // ignored
            }

            catch (AggregateException ex)
            {
                if (!(ex.InnerExceptions.First() is PoolStartupAbortException))
                    Console.WriteLine(ex);

                Console.WriteLine("Cluster cannot start. Good Bye!");
            }

            catch (OperationCanceledException)
            {
                // Ctrl+C
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex);

                Console.WriteLine("Cluster cannot start. Good Bye!");
            }

            Shutdown();
            Process.GetCurrentProcess().CloseMainWindow();
            Process.GetCurrentProcess().Close();
        }

        private static void LogRuntimeInfo()
        {
            logger.Info(() => $"{RuntimeInformation.FrameworkDescription.Trim()} on {RuntimeInformation.OSDescription.Trim()} [{RuntimeInformation.ProcessArchitecture}]");
        }

        private static void ValidateConfig()
        {
            // set some defaults
            foreach (var config in clusterConfig.Pools)
            {
                if (!config.EnableInternalStratum.HasValue)
                    config.EnableInternalStratum = config.ExternalStratums == null || config.ExternalStratums.Length == 0;
            }

            try
            {
                clusterConfig.Validate();
            }
            catch (ValidationException ex)
            {
                Console.WriteLine($"Configuration is not valid:\n\n{string.Join("\n", ex.Errors.Select(x => "=> " + x.ErrorMessage))}");
                throw new PoolStartupAbortException(string.Empty);
            }
        }

        private static void DumpParsedConfig(ClusterConfig config)
        {
            Console.WriteLine("\nCurrent configuration as parsed from config file:");

            Console.WriteLine(JsonConvert.SerializeObject(config, new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented
            }));
        }

        private static bool HandleCommandLineOptions(string[] args, out string configFile)
        {
            configFile = null;

            var app = new CommandLineApplication(false)
            {
                FullName = "MiningHub.Core - Pool Mining Engine",
                ShortVersionGetter = () => $"v{Assembly.GetEntryAssembly().GetName().Version}",
                LongVersionGetter = () => $"v{Assembly.GetEntryAssembly().GetName().Version}"
            };

            var versionOption = app.Option("-v|--version", "Version Information", CommandOptionType.NoValue);
            var configFileOption = app.Option("-c|--config <configfile>", "Configuration File",
                CommandOptionType.SingleValue);
            dumpConfigOption = app.Option("-dc|--dumpconfig",
                "Dump the configuration (useful for trouble-shooting typos in the config file)",
                CommandOptionType.NoValue);
            shareRecoveryOption = app.Option("-rs", "Import lost shares using existing recovery file",
                CommandOptionType.SingleValue);
            app.HelpOption("-? | -h | --help");

            app.Execute(args);

            if (versionOption.HasValue())
            {
                app.ShowVersion();
                return false;
            }

            if (!configFileOption.HasValue())
            {
                app.ShowHelp();
                return false;
            }

            configFile = configFileOption.Value();

            return true;
        }

        private static void Bootstrap()
        {
            // Service collection
            var builder = new ContainerBuilder();

            builder.RegisterAssemblyModules(typeof(AutofacModule).GetTypeInfo().Assembly);
            builder.RegisterInstance(clusterConfig);

            // AutoMapper
            var amConf = new MapperConfiguration(cfg => { cfg.AddProfile(new AutoMapperProfile()); });
            builder.Register((ctx, parms) => amConf.CreateMapper());

            ConfigurePersistence(builder);
            container = builder.Build();
            ConfigureLogging();
            ValidateRuntimeEnvironment();
            MonitorGc();
        }

        private static ClusterConfig ReadConfig(string file)
        {
            try
            {
                Console.WriteLine($"Using configuration file {file}\n");

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

                using (var reader = new StreamReader(file, Encoding.UTF8))
                {
                    using (var jsonReader = new JsonTextReader(reader))
                    {
                        return serializer.Deserialize<ClusterConfig>(jsonReader);
                    }
                }
            }

            catch (JsonSerializationException ex)
            {
                HumanizeJsonParseException(ex);
                throw;
            }

            catch (JsonException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }

            catch (IOException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        private static void HumanizeJsonParseException(JsonSerializationException ex)
        {
            var m = regexJsonTypeConversionError.Match(ex.Message);

            if (m.Success)
            {
                var value = m.Groups[1].Value;
                var type = Type.GetType(m.Groups[2].Value);
                var line = m.Groups[3].Value;
                var col = m.Groups[4].Value;

                if (type == typeof(CoinType))
                    Console.WriteLine($"Error: Coin '{value}' is not (yet) supported (line {line}, column {col})");
                else if (type == typeof(PayoutScheme))
                    Console.WriteLine(
                        $"Error: Payout scheme '{value}' is not (yet) supported (line {line}, column {col})");
            }

            else
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private static void ValidateRuntimeEnvironment()
        {
            // root check
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.UserName == "root")
                logger.Warn(() => "Running as root is discouraged!");
        }

        private static void MonitorGc()
        {
            var thread = new Thread(() =>
            {
                var sw = new Stopwatch();

                while (true)
                {
                    var s = GC.WaitForFullGCApproach();
                    if (s == GCNotificationStatus.Succeeded)
                    {
                        logger.Info(() => "FullGC soon");
                        sw.Start();
                    }

                    s = GC.WaitForFullGCComplete();

                    if (s == GCNotificationStatus.Succeeded)
                    {
                        logger.Info(() => "FullGC completed");

                        sw.Stop();

                        if (sw.Elapsed.TotalSeconds > gcStats.MaxFullGcDuration)
                            gcStats.MaxFullGcDuration = sw.Elapsed.TotalSeconds;

                        sw.Reset();
                    }
                }
            });

            GC.RegisterForFullGCNotification(1, 1);
            thread.Start();
        }

        private static void Logo()
        {
            Console.WriteLine($@"
  __  _____   ________     ___  ____  ____  __ 
 / / / / _ ) /  _/ __ \   / _ \/ __ \/ __ \/ / 
/ /_/ / _  |_/ // /_/ /  / ___/ /_/ / /_/ / /__
\____/____//___/\___\_\ /_/   \____/\____/____/
");
            Console.WriteLine($" https://github.com/erikrijn/mininghub\n");
            Console.WriteLine($" Please contribute to the development of the project by donating:\n");
            Console.WriteLine($" ETH  - 0x8af99f924e1b2f3dcc0b2fc706d4c595d236ce7c\n");
            Console.WriteLine($" UBQ  - 0x8af99f924e1b2f3dcc0b2fc706d4c595d236ce7c");
            Console.WriteLine();
        }

        private static void ConfigureLogging()
        {
            var config = clusterConfig.Logging;
            var loggingConfig = new LoggingConfiguration();

            if (config != null)
            {
                // parse level
                var level = !string.IsNullOrEmpty(config.Level)
                    ? LogLevel.FromString(config.Level)
                    : LogLevel.Info;

                var layout = "[${longdate}] [${level:format=FirstCharacter:uppercase=true}] [${logger:shortName=true}] ${message} ${exception:format=ToString,StackTrace}";

                if (config.EnableConsoleLog)
                {
                    if (config.EnableConsoleColors)
                    {
                        var target = new ColoredConsoleTarget("console")
                        {
                            Layout = layout
                        };

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Trace"),
                            ConsoleOutputColor.DarkMagenta, ConsoleOutputColor.NoChange));

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Debug"),
                            ConsoleOutputColor.Gray, ConsoleOutputColor.NoChange));

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Info"),
                            ConsoleOutputColor.White, ConsoleOutputColor.NoChange));

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Warn"),
                            ConsoleOutputColor.Yellow, ConsoleOutputColor.NoChange));

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Error"),
                            ConsoleOutputColor.Red, ConsoleOutputColor.NoChange));

                        target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                            ConditionParser.ParseExpression("level == LogLevel.Fatal"),
                            ConsoleOutputColor.DarkRed, ConsoleOutputColor.White));

                        loggingConfig.AddTarget(target);
                        loggingConfig.AddRule(level, LogLevel.Fatal, target);
                    }

                    else
                    {
                        var target = new ConsoleTarget("console")
                        {
                            Layout = layout
                        };

                        loggingConfig.AddTarget(target);
                        loggingConfig.AddRule(level, LogLevel.Fatal, target);
                    }
                }

                if (!string.IsNullOrEmpty(config.LogFile))
                {
                    var target = new FileTarget("file")
                    {
                        FileName = GetLogPath(config, config.LogFile),
                        FileNameKind = FilePathKind.Unknown,
                        Layout = layout
                    };

                    loggingConfig.AddTarget(target);
                    loggingConfig.AddRule(level, LogLevel.Fatal, target);
                }

                if (config.PerPoolLogFile)
                {
                    foreach (var poolConfig in clusterConfig.Pools)
                    {
                        var target = new FileTarget(poolConfig.Id)
                        {
                            FileName = GetLogPath(config, poolConfig.Id + ".log"),
                            FileNameKind = FilePathKind.Unknown,
                            Layout = layout
                        };

                        loggingConfig.AddTarget(target);
                        loggingConfig.AddRule(level, LogLevel.Fatal, target, poolConfig.Id);
                    }
                }
            }

            LogManager.Configuration = loggingConfig;

            logger = LogManager.GetCurrentClassLogger();
        }

        private static Layout GetLogPath(ClusterLoggingConfig config, string name)
        {
            if (string.IsNullOrEmpty(config.LogBaseDirectory))
                return name;

            return Path.Combine(config.LogBaseDirectory, name);
        }

        private static void ConfigurePersistence(ContainerBuilder builder)
        {
            if (clusterConfig.Persistence == null &&
                clusterConfig.PaymentProcessing?.Enabled == true &&
                clusterConfig.ShareRelay == null)
                logger.ThrowLogPoolStartupException("Persistence is not configured!");

            if (clusterConfig.Persistence?.Postgres != null)
                ConfigurePostgres(clusterConfig.Persistence.Postgres, builder);
            else
                ConfigureDummyPersistence(builder);
        }

        private static void ConfigurePostgres(DatabaseConfig pgConfig, ContainerBuilder builder)
        {
            // validate config
            if (string.IsNullOrEmpty(pgConfig.Host))
                logger.ThrowLogPoolStartupException("Postgres configuration: invalid or missing 'host'");

            if (pgConfig.Port == 0)
                logger.ThrowLogPoolStartupException("Postgres configuration: invalid or missing 'port'");

            if (string.IsNullOrEmpty(pgConfig.Database))
                logger.ThrowLogPoolStartupException("Postgres configuration: invalid or missing 'database'");

            if (string.IsNullOrEmpty(pgConfig.User))
                logger.ThrowLogPoolStartupException("Postgres configuration: invalid or missing 'user'");

            // build connection string
            var connectionString = $"Server={pgConfig.Host};Port={pgConfig.Port};Database={pgConfig.Database};User Id={pgConfig.User};Password={pgConfig.Password};CommandTimeout=900;";

            // register connection factory
            builder.RegisterInstance(new PgConnectionFactory(connectionString))
                .AsImplementedInterfaces();

            // register repositories
            builder.RegisterAssemblyTypes(Assembly.GetExecutingAssembly())
                .Where(t =>
                    t.Namespace.StartsWith(typeof(ShareRepository).Namespace))
                .AsImplementedInterfaces()
                .SingleInstance();
        }

        private static void ConfigureDummyPersistence(ContainerBuilder builder)
        {
            // register connection factory
            builder.RegisterInstance(new DummyConnectionFactory(string.Empty))
                .AsImplementedInterfaces();

            // register repositories
            builder.RegisterAssemblyTypes(Assembly.GetExecutingAssembly())
                .Where(t =>
                    t.Namespace.StartsWith(typeof(ShareRepository).Namespace))
                .AsImplementedInterfaces()
                .SingleInstance();
        }

        private static async Task Start()
        {
            if (clusterConfig.ShareRelay == null)
            {
                // start share recorder
                shareRecorder = container.Resolve<ShareRecorder>();
                shareRecorder.Start(clusterConfig);

                // start share receiver (for external shares)
                shareReceiver = container.Resolve<ShareReceiver>();
                shareReceiver.Start(clusterConfig);
            }

            else
            {
                // start share relay
                shareRelay = container.Resolve<ShareRelay>();
                shareRelay.Start(clusterConfig);
            }

            // start API
            if (clusterConfig.Api == null || clusterConfig.Api.Enabled)
            {
                apiServer = container.Resolve<ApiServer>();
                apiServer.Start(clusterConfig);
            }

            // start payment processor
            if (clusterConfig.PaymentProcessing?.Enabled == true &&
                clusterConfig.Pools.Any(x => x.PaymentProcessing?.Enabled == true))
            {
                payoutManager = container.Resolve<PayoutManager>();
                payoutManager.Configure(clusterConfig);

                payoutManager.Start();
            }

            else
                logger.Info("Payment processing is not enabled");

            if (clusterConfig.ShareRelay == null && clusterConfig.Logging.StatsRecorderEnabled)
            {
                // start pool stats updater
                statsRecorder = container.Resolve<StatsRecorder>();
                statsRecorder.Configure(clusterConfig);
                statsRecorder.Start();
            }

            // start pools
            await Task.WhenAll(clusterConfig.Pools.Where(x => x.Enabled).Select(async poolConfig =>
            {
                // resolve pool implementation
                var poolImpl = container.Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinMetadataAttribute>>>>()
                    .First(x => x.Value.Metadata.SupportedCoins.Contains(poolConfig.Coin.Type)).Value;

                // create and configure
                var pool = poolImpl.Value;
                pool.Configure(poolConfig, clusterConfig);

                // pre-start attachments
                shareReceiver?.AttachPool(pool);
                statsRecorder?.AttachPool(pool);

                await pool.StartAsync(cts.Token);
            }));

            // keep running
            await Observable.Never<Unit>().ToTask(cts.Token);
        }

        private static void RecoverShares(string recoveryFilename)
        {
            shareRecorder = container.Resolve<ShareRecorder>();
            shareRecorder.RecoverShares(clusterConfig, recoveryFilename);
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (logger != null)
            {
                logger.Error(e.ExceptionObject);
                LogManager.Flush(TimeSpan.Zero);
            }

            Console.WriteLine("** AppDomain unhandled exception: {0}", e.ExceptionObject);
        }

        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            logger?.Info(() => "SIGINT received. Exiting.");
            Console.WriteLine("SIGINT received. Exiting.");

            try
            {
                cts?.Cancel();
            }
            catch { }

            e.Cancel = true;
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            logger?.Info(() => "SIGTERM received. Exiting.");
            Console.WriteLine("SIGTERM received. Exiting.");

            try
            {
                cts?.Cancel();
            }
            catch { }
        }

        private static void Shutdown()
        {
            logger.Info(() => "Shutdown ...");
            Console.WriteLine("Shutdown...");

            shareRelay?.Stop();
            shareRecorder?.Stop();
            statsRecorder?.Stop();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull, uint dwFlags);

        private static readonly string[] NativeLibs =
        {
            "libmultihash.dll",
            "libcryptonote.dll"
        };

        /// <summary>
        /// work-around for libmultihash.dll not being found when running in dev-environment
        /// </summary>
        public static void PreloadNativeLibs()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine($"{nameof(PreloadNativeLibs)} only operates on Windows");
                return;
            }

            // load it
            var runtime = Environment.Is64BitProcess ? "win-x64" : "win-86";
            var appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            foreach (var nativeLib in NativeLibs)
            {
                var path = Path.Combine(appRoot, "runtimes", runtime, "native", nativeLib);
                var result = LoadLibraryEx(path, IntPtr.Zero, 0);

                if (result == IntPtr.Zero)
                    Console.WriteLine($"Unable to load {path}");
            }
        }
    }
}

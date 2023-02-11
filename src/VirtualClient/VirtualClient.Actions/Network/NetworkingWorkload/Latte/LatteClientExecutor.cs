﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions.NetworkPerformance
{
    using System;
    using System.Collections.Generic;
    using System.IO.Abstractions;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using VirtualClient.Common;
    using VirtualClient.Common.Platform;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// Latte client-side workload executor.
    /// </summary>
    [WindowsCompatible]
    public class LatteClientExecutor : LatteExecutor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LatteClientExecutor"/> class.
        /// </summary>
        /// <param name="dependencies">Provides required dependencies to the component.</param>
        /// <param name="parameters">Parameters defined in the profile or supplied on the command line.</param>
        public LatteClientExecutor(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters)
           : base(dependencies, parameters)
        {
            this.WorkloadEmitsResults = true;
        }

        /// <inheritdoc/>
        protected override Task<IProcessProxy> ExecuteWorkloadAsync(string commandArguments, TimeSpan timeout, EventContext telemetryContext, CancellationToken cancellationToken)
        {
            IProcessProxy process = null;

            EventContext relatedContext = telemetryContext.Clone()
               .AddContext("command", this.ExecutablePath)
               .AddContext("commandArguments", commandArguments);

            return this.Logger.LogMessageAsync($"{this.TypeName}.ExecuteWorkload", relatedContext, async () =>
            {
                using (BackgroundOperations profiling = BackgroundOperations.BeginProfiling(this, cancellationToken))
                {
                    await this.ProcessStartRetryPolicy.ExecuteAsync(async () =>
                    {
                        using (process = this.SystemManagement.ProcessManager.CreateProcess(this.ExecutablePath, commandArguments))
                        {
                            try
                            {
                                this.CleanupTasks.Add(() => process.SafeKill());
                                await process.StartAndWaitAsync(cancellationToken, timeout);

                                if (!cancellationToken.IsCancellationRequested)
                                {
                                    await this.LogProcessDetailsAsync(process, telemetryContext, "Latte", logToFile: true);
                                    process.ThrowIfErrored<WorkloadException>(errorReason: ErrorReason.WorkloadFailed);
                                    await this.SystemManagement.FileSystem.File.WriteAllTextAsync(this.ResultsPath, process.StandardOutput.ToString());
                                }
                            }
                            catch (TimeoutException exc)
                            {
                                // We give this a best effort but do not want it to prevent the next workload
                                // from executing.
                                this.Logger.LogMessage($"{this.GetType().Name}.WorkloadTimeout", LogLevel.Warning, relatedContext.AddError(exc));
                                process.SafeKill();
                            }
                            catch (Exception exc)
                            {
                                this.Logger.LogMessage($"{this.GetType().Name}.WorkloadStartupError", LogLevel.Warning, relatedContext.AddError(exc));
                                process.SafeKill();
                                throw;
                            }
                        }
                    }).ConfigureAwait(false);
                }

                return process;
            });
        }

        /// <summary>
        /// Returns the Latte client-side command line arguments.
        /// </summary>
        protected override string GetCommandLineArguments()
        {
            string clientIPAddress = this.GetLayoutClientInstances(ClientRole.Client).First().IPAddress;
            string serverIPAddress = this.GetLayoutClientInstances(ClientRole.Server).First().IPAddress;

            return $"-so -c -a {serverIPAddress}:{this.Port} -rio -i {this.Iterations} -riopoll {this.RioPoll} -{this.Protocol.ToString().ToLowerInvariant()} " +
            $"-hist -hl 1 -hc 9998 -bl {clientIPAddress}";
        }

        /// <summary>
        /// Logs the workload metrics to the telemetry.
        /// </summary>
        protected override async Task CaptureMetricsAsync(string commandArguments, DateTime startTime, DateTime endTime, EventContext telemetryContext)
        {
            IFile fileAccess = this.SystemManagement.FileSystem.File;

            if (fileAccess.Exists(this.ResultsPath))
            {
                string resultsContent = await this.SystemManagement.FileSystem.File.ReadAllTextAsync(this.ResultsPath)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resultsContent))
                {
                    MetricsParser parser = new LatteMetricsParser(resultsContent);
                    IList<Metric> metrics = parser.Parse();

                    this.Logger.LogMetrics(
                        this.Tool.ToString(),
                        this.Name,
                        startTime,
                        endTime,
                        metrics,
                        string.Empty,
                        commandArguments,
                        this.Tags,
                        telemetryContext,
                        resultsContent);
                }
            }
        }
    }
}

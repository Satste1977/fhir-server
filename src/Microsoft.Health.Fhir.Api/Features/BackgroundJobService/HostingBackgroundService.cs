﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.JobManagement;

namespace Microsoft.Health.Fhir.Api.Features.BackgroundJobService
{
    /// <summary>
    /// The background service used to host the <see cref="JobHosting"/>.
    /// </summary>
    public class HostingBackgroundService : BackgroundService
    {
        private readonly Func<IScoped<JobHosting>> _jobHostingFactory;
        private readonly OperationsConfiguration _operationsConfiguration;
        private readonly TaskHostingConfiguration _hostingConfiguration;
        private readonly ILogger<HostingBackgroundService> _logger;

        public HostingBackgroundService(
            Func<IScoped<JobHosting>> jobHostingFactory,
            IOptions<TaskHostingConfiguration> hostingConfiguration,
            IOptions<OperationsConfiguration> operationsConfiguration,
            ILogger<HostingBackgroundService> logger)
        {
            EnsureArg.IsNotNull(jobHostingFactory, nameof(jobHostingFactory));
            EnsureArg.IsNotNull(hostingConfiguration?.Value, nameof(hostingConfiguration));
            EnsureArg.IsNotNull(operationsConfiguration?.Value, nameof(operationsConfiguration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _jobHostingFactory = jobHostingFactory;
            _operationsConfiguration = operationsConfiguration.Value;
            _hostingConfiguration = hostingConfiguration.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HostingBackgroundService begin.");

            try
            {
                using IScoped<JobHosting> jobHosting = _jobHostingFactory();
                JobHosting jobHostingValue = jobHosting.Value;
                if (_hostingConfiguration != null)
                {
                    jobHostingValue.PollingFrequencyInSeconds = _hostingConfiguration.PollingFrequencyInSeconds ?? jobHostingValue.PollingFrequencyInSeconds;
                    jobHostingValue.MaxRunningJobCount = _hostingConfiguration.MaxRunningTaskCount ?? jobHostingValue.MaxRunningJobCount;
                    jobHostingValue.JobHeartbeatIntervalInSeconds = _hostingConfiguration.TaskHeartbeatIntervalInSeconds ?? jobHostingValue.JobHeartbeatIntervalInSeconds;
                    jobHostingValue.JobHeartbeatTimeoutThresholdInSeconds = _hostingConfiguration.TaskHeartbeatTimeoutThresholdInSeconds ?? jobHostingValue.JobHeartbeatTimeoutThresholdInSeconds;
                }

                using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var jobQueues = new List<Task>();

                foreach (var operation in _operationsConfiguration.HostingBackgroundServiceQueues)
                {
                    jobQueues.Add(jobHostingValue.ExecuteAsync((byte)operation.Queue, Environment.MachineName, cancellationTokenSource, operation.UpdateProgressOnHeartbeat));
                }

                await Task.WhenAll(jobQueues);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HostingBackgroundService crash.");
            }
            finally
            {
                _logger.LogInformation("HostingBackgroundService end.");
            }
        }
    }
}

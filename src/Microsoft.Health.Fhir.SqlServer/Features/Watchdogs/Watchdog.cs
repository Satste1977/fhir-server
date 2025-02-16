﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.SqlServer.Features.Client;

namespace Microsoft.Health.Fhir.SqlServer.Features.Watchdogs
{
    public abstract class Watchdog<T> : FhirTimer<T>
    {
        private Func<IScoped<SqlConnectionWrapperFactory>> _sqlConnectionWrapperFactory;
        private readonly ILogger<T> _logger;
        private readonly WatchdogLease<T> _watchdogLease;
        private bool _disposed = false;
        private double _periodSec;
        private double _leasePeriodSec;

        protected Watchdog(Func<IScoped<SqlConnectionWrapperFactory>> sqlConnectionWrapperFactory, ILogger<T> logger)
            : base(logger)
        {
            _sqlConnectionWrapperFactory = EnsureArg.IsNotNull(sqlConnectionWrapperFactory, nameof(sqlConnectionWrapperFactory));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _watchdogLease = new WatchdogLease<T>(_sqlConnectionWrapperFactory, _logger);
        }

        protected Watchdog()
        {
            // this is used to get param names for testing
        }

        internal string Name => GetType().Name;

        internal string PeriodSecId => $"{Name}.PeriodSec";

        internal string LeasePeriodSecId => $"{Name}.LeasePeriodSec";

        internal bool IsLeaseHolder => _watchdogLease.IsLeaseHolder;

        internal string LeaseWorker => _watchdogLease.Worker;

        internal double LeasePeriodSec => _watchdogLease.PeriodSec;

        protected internal async Task StartAsync(bool allowRebalance, double periodSec, double leasePeriodSec, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Watchdog.StartAsync: starting...");
            await InitParamsAsync(periodSec, leasePeriodSec);
            await StartAsync(_periodSec, cancellationToken);
            await _watchdogLease.StartAsync(allowRebalance, _leasePeriodSec, cancellationToken);
            _logger.LogInformation("Watchdog.StartAsync: completed.");
        }

        protected abstract Task ExecuteAsync();

        protected override async Task RunAsync()
        {
            if (!_watchdogLease.IsLeaseHolder)
            {
                _logger.LogInformation($"Watchdog.RunAsync: Skipping because watchdog is not a lease holder.");
                return;
            }

            _logger.LogInformation($"Watchdog.RunAsync: Starting...");
            await ExecuteAsync();
            _logger.LogInformation($"Watchdog.RunAsync: Completed.");
        }

        private async Task InitParamsAsync(double periodSec, double leasePeriodSec) // No CancellationToken is passed since we shouldn't cancel initialization.
        {
            _logger.LogInformation("Watchdog.InitParamsAsync: starting...");

            // Offset for other instances running init
            await Task.Delay(TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(10) / 10.0), CancellationToken.None);

            using IScoped<SqlConnectionWrapperFactory> scopedConn = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedConn.Value.ObtainSqlConnectionWrapperAsync(CancellationToken.None, false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();
            cmd.CommandText = @"
INSERT INTO dbo.Parameters (Id,Number) SELECT @PeriodSecId, @PeriodSec
INSERT INTO dbo.Parameters (Id,Number) SELECT @LeasePeriodSecId, @LeasePeriodSec
            ";
            cmd.Parameters.AddWithValue("@PeriodSecId", PeriodSecId);
            cmd.Parameters.AddWithValue("@PeriodSec", periodSec);
            cmd.Parameters.AddWithValue("@LeasePeriodSecId", LeasePeriodSecId);
            cmd.Parameters.AddWithValue("@LeasePeriodSec", leasePeriodSec);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);

            _periodSec = await GetPeriodAsync(CancellationToken.None);
            _leasePeriodSec = await GetLeasePeriodAsync(CancellationToken.None);

            _logger.LogInformation("Watchdog.InitParamsAsync: completed.");
        }

        private async Task<double> GetPeriodAsync(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterByIdAsync(PeriodSecId, cancellationToken);
            return value;
        }

        private async Task<double> GetLeasePeriodAsync(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterByIdAsync(LeasePeriodSecId, cancellationToken);
            return value;
        }

        protected async Task<double> GetNumberParameterByIdAsync(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrEmpty(id, nameof(id));

            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();

            cmd.CommandText = "SELECT Number FROM dbo.Parameters WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);

            if (value == null)
            {
                throw new InvalidOperationException($"{id} is not set correctly in the Parameters table.");
            }

            return (double)value;
        }

        public new void Dispose()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _watchdogLease?.Dispose();
            }

            base.Dispose(disposing);

            _disposed = true;
        }
    }
}

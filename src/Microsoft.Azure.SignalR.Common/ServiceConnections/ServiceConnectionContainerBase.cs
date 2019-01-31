﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.SignalR.Common;
using Microsoft.Azure.SignalR.Protocol;

namespace Microsoft.Azure.SignalR
{
    abstract class ServiceConnectionContainerBase : IServiceConnectionContainer, IServiceMessageHandler
    {
        private static readonly int MaxReconnectBackOffInternalInMilliseconds = 1000;
        private static TimeSpan ReconnectInterval =>
            TimeSpan.FromMilliseconds(StaticRandom.Next(MaxReconnectBackOffInternalInMilliseconds));

        private readonly ServiceEndpoint _endpoint;

        protected readonly IServiceConnectionFactory ServiceConnectionFactory;
        protected readonly IConnectionFactory ConnectionFactory;
        protected readonly ConcurrentDictionary<int?, IServiceConnection> FixedServiceConnections;
        protected readonly int FixedConnectionCount;

        private volatile int _defaultConnectionRetry;

        protected ServiceConnectionContainerBase(IServiceConnectionFactory serviceConnectionFactory,
            IConnectionFactory connectionFactory,
            int fixedConnectionCount, ServiceEndpoint endpoint)
        {
            ServiceConnectionFactory = serviceConnectionFactory;
            ConnectionFactory = connectionFactory;
            FixedServiceConnections = CreateFixedServiceConnection(fixedConnectionCount);
            FixedConnectionCount = fixedConnectionCount;
            _endpoint = endpoint;
        }

        protected ServiceConnectionContainerBase(IServiceConnectionFactory serviceConnectionFactory,
            IConnectionFactory connectionFactory, List<IServiceConnection> initialConnections, ServiceEndpoint endpoint)
        {
            ServiceConnectionFactory = serviceConnectionFactory;
            ConnectionFactory = connectionFactory;
            FixedServiceConnections = new ConcurrentDictionary<int?, IServiceConnection>(initialConnections.Select((connection, i) => new {connection, i}).ToDictionary(x => (int?)x.i, x => x.connection));
            FixedConnectionCount = initialConnections.Count;
            _endpoint = endpoint;
        }

        public async Task StartAsync()
        {
            var task = Task.WhenAll(FixedServiceConnections.Select(c => StartCoreAsync(c.Value)));
            await Task.WhenAny(FixedServiceConnections.Select(s => s.Value.ConnectionInitializedTask));

            // Set the endpoint connection after one connection is initialized
            if (_endpoint != null)
            {
                _endpoint.Connection = this;
            }

            await task;
        }

        /// <summary>
        /// Start and manage the whole connection lifetime
        /// </summary>
        /// <returns></returns>
        protected async Task StartCoreAsync(IServiceConnection connection, string target = null)
        {
            try
            {
                await connection.StartAsync(target);
            }
            finally
            {
                await DisposeOrRestartServiceConnectionAsync(connection);
            }
        }

        public abstract Task HandlePingAsync(string target);

        /// <summary>
        /// Create a connection in initialization and reconnection
        /// </summary>
        protected abstract IServiceConnection CreateServiceConnectionCore();

        /// <summary>
        /// Create a connection for a specific service connection type
        /// </summary>
        protected virtual IServiceConnection CreateServiceConnectionCore(ServerConnectionType type)
        {
            return ServiceConnectionFactory.Create(ConnectionFactory, this, type);
        }

        protected abstract Task DisposeOrRestartServiceConnectionAsync(IServiceConnection connection);

        protected async Task RestartServiceConnectionCoreAsync(int index)
        {
            await Task.Delay(GetRetryDelay(_defaultConnectionRetry));

            // Increase retry count after delay, then if a group of connections get disconnected simultaneously,
            // all of them will delay a similar range of time and reconnect. But if they get disconnected again (when SignalR service down), 
            // they will all delay for a much longer time.
            Interlocked.Increment(ref _defaultConnectionRetry);

            var connection = CreateServiceConnectionCore();
            FixedServiceConnections.AddOrUpdate(index, connection, (_, __) => connection);

            _ = StartCoreAsync(connection);
            await connection.ConnectionInitializedTask;

            if (connection.Status == ServiceConnectionStatus.Connected)
            {
                Interlocked.Exchange(ref _defaultConnectionRetry, 0);
            }
        }

        internal static TimeSpan GetRetryDelay(int retryCount)
        {
            // retry count:   0, 1, 2, 3, 4,  5,  6,  ...
            // delay seconds: 1, 2, 4, 8, 16, 32, 60, ...
            if (retryCount > 5)
            {
                return TimeSpan.FromMinutes(1) + ReconnectInterval;
            }
            return TimeSpan.FromSeconds(1 << retryCount) + ReconnectInterval;
        }

        public ServiceConnectionStatus Status => GetStatus();

        public Task WriteAsync(ServiceMessage serviceMessage)
        {
            return WriteToRandomAvailableConnection(serviceMessage);
        }

        public Task WriteAsync(string partitionKey, ServiceMessage serviceMessage)
        {
            // If we hit this check, it is a code bug.
            if (string.IsNullOrEmpty(partitionKey))
            {
                throw new ArgumentNullException(nameof(partitionKey));
            }

            return WriteToPartitionedConnection(partitionKey, serviceMessage);
        }

        protected virtual ServiceConnectionStatus GetStatus()
        {
            return FixedServiceConnections.Any(s => s.Value.Status == ServiceConnectionStatus.Connected)
                ? ServiceConnectionStatus.Connected
                : ServiceConnectionStatus.Disconnected;
        }

        private Task WriteToPartitionedConnection(string partitionKey, ServiceMessage serviceMessage)
        {
            return WriteWithRetry(serviceMessage, partitionKey.GetHashCode(), FixedConnectionCount);
        }

        private Task WriteToRandomAvailableConnection(ServiceMessage serviceMessage)
        {
            return WriteWithRetry(serviceMessage, StaticRandom.Next(-FixedConnectionCount, FixedConnectionCount), FixedConnectionCount);
        }

        private async Task WriteWithRetry(ServiceMessage serviceMessage, int initial, int count)
        {
            // go through all the connections, it can be useful when one of the remote service instances is down
            var maxRetry = count;
            var retry = 0;
            var index = (initial & int.MaxValue) % count;
            var direction = initial > 0 ? 1 : count - 1;
            while (retry < maxRetry)
            {
                FixedServiceConnections.TryGetValue(index, out var connection);
                if (connection != null && connection.Status == ServiceConnectionStatus.Connected)
                {
                    try
                    {
                        // still possible the connection is not valid
                        await connection.WriteAsync(serviceMessage);
                        return;
                    }
                    catch (ServiceConnectionNotActiveException)
                    {
                        if (retry == maxRetry - 1)
                        {
                            throw;
                        }
                    }
                }

                retry++;
                index = (index + direction) % count;
            }

            throw new ServiceConnectionNotActiveException();
        }

        private ConcurrentDictionary<int?, IServiceConnection> CreateFixedServiceConnection(int count)
        {
            var connections = new ConcurrentDictionary<int?, IServiceConnection>();
            for (int i = 0; i < count; i++)
            {
                var connection = CreateServiceConnectionCore();
                connections.TryAdd(i, connection);
            }

            return connections;
        }
    }
}

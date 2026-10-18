using System;
using System.IO;
using System.Net.Sockets;
using System.ServiceModel;
using Interlock.Core.Errors;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The connection to one OIB service, shared by the clients: creates the proxy on the first call, reuses it, and applies the
    /// failure policy.
    /// <list type="bullet">
    /// <item>A connection failure (communication or timeout exception, a broken stream or socket, a disposed channel, or a
    /// failure to create the proxy) drops the proxy, closes it quietly and starts the <see cref="ReconnectBackoff"/> wait.
    /// While that wait runs, calls throw <see cref="OibCallException"/> "&lt;service&gt; not connected" without creating a
    /// proxy or touching the network.</item>
    /// <item>A fault the service sent back (<see cref="FaultException"/>) or any other exception is a business failure: it is
    /// wrapped in <see cref="OibCallException"/> but the proxy stays and the backoff is not touched.</item>
    /// <item>A call that returns resets the backoff only if it proves the connection works. Business calls always do. A health
    /// probe says so through <c>provesConnection</c>: <c>IsConnected</c> answers false (without throwing) when the service is
    /// down, so a false answer proves nothing and leaves the backoff as it is. A returned false is not a connection failure
    /// either, so it does not start the wait.</item>
    /// </list>
    /// The proxy is created and replaced under a lock, so concurrent first calls create one proxy. Creation (<c>create</c>) runs
    /// under that lock and may do network I/O, so concurrent callers wait for it. The lock is never held while a call runs or
    /// while a dead proxy is closed.
    /// </summary>
    internal sealed class OibConnection<TProxy> : IDisposable where TProxy : class
    {
        private readonly string _service;
        private readonly Func<TProxy> _create;
        private readonly Action<TProxy> _close;
        private readonly Action<TProxy> _open;
        private readonly ReconnectBackoff _backoff;
        private readonly object _gate = new object();
        private TProxy _proxy;
        private bool _disposed;

        /// <param name="service">The name used in messages, for example "Setup Center".</param>
        /// <param name="create">Makes a new proxy. It may throw.</param>
        /// <param name="close">Releases a proxy. Its exceptions are ignored.</param>
        /// <param name="open">
        /// Opens a new proxy: a harmless first call. WCF channels open themselves on the first call and that open is not safe to
        /// race, so it runs once, right after creation and under the creation lock; the proxy is shared with other threads only
        /// when it succeeded. A failure counts as a connection failure. Omitted: no open step.
        /// </param>
        public OibConnection(string service, Func<TProxy> create, Action<TProxy> close, IClock clock, Action<TProxy> open = null)
        {
            if (string.IsNullOrWhiteSpace(service))
                throw new ArgumentException("A service name is required.", nameof(service));
            if (create == null)
                throw new ArgumentNullException(nameof(create));
            if (close == null)
                throw new ArgumentNullException(nameof(close));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _service = service;
            _create = create;
            _close = close;
            _open = open;
            _backoff = new ReconnectBackoff(clock);
        }

        /// <summary>Runs <paramref name="call"/> on the proxy. Every failure is an <see cref="OibCallException"/>.</summary>
        /// <param name="provesConnection">
        /// Decides from the result whether the call proves the connection works (and so resets the backoff). Omitted: it always does.
        /// </param>
        public T Call<T>(string action, Func<TProxy, T> call, Func<T, bool> provesConnection = null)
        {
            var proxy = Acquire(action);
            T result;
            try
            {
                result = call(proxy);
            }
            catch (OibCallException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsConnectionFailure(ex))
                    Drop(proxy);
                throw new OibCallException(_service + " " + action + " failed: " + ex.Message, ex);
            }

            if (provesConnection == null || provesConnection(result))
            {
                lock (_gate)
                    _backoff.Succeeded();
            }
            return result;
        }

        public void Call(string action, Action<TProxy> call)
        {
            Call<object>(action, p =>
            {
                call(p);
                return null;
            });
        }

        public void Dispose()
        {
            TProxy old;
            lock (_gate)
            {
                _disposed = true;
                old = _proxy;
                _proxy = null;
            }
            Close(old);
        }

        private TProxy Acquire(string action)
        {
            TProxy failed = null;
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                        throw new OibCallException(_service + " client is disposed");
                    if (_proxy != null)
                        return _proxy;
                    if (!_backoff.CanTry)
                        throw new OibCallException(_service + " not connected");

                    try
                    {
                        var created = _create();
                        failed = created;
                        if (_open != null)
                            _open(created);
                        failed = null;
                        _proxy = created;
                        return created;
                    }
                    catch (Exception ex)
                    {
                        _backoff.Failed();
                        throw new OibCallException(_service + " " + action + " failed: " + ex.Message, ex);
                    }
                }
            }
            finally
            {
                // A proxy whose open failed was never shared; close it outside the lock.
                Close(failed);
            }
        }

        /// <summary>
        /// Drops <paramref name="proxy"/> and starts the wait, unless another call already did. Two calls that fail together on
        /// one proxy count as one failure.
        /// </summary>
        private void Drop(TProxy proxy)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_proxy, proxy))
                    return;
                _proxy = null;
                _backoff.Failed();
            }
            Close(proxy);
        }

        private void Close(TProxy proxy)
        {
            if (proxy == null)
                return;
            try
            {
                _close(proxy);
            }
            catch (Exception)
            {
                // A proxy that cannot be closed is gone either way.
            }
        }

        /// <summary>
        /// True when the exception, or one inside it, says the link to the service is broken. A <see cref="FaultException"/> is the
        /// service answering, so it stops the search: the connection works.
        /// </summary>
        private static bool IsConnectionFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is FaultException)
                    return false;
                if (e is CommunicationException || e is TimeoutException || e is IOException || e is SocketException || e is ObjectDisposedException)
                    return true;
            }
            return false;
        }
    }
}

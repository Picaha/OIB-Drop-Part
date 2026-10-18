using System.Threading;

namespace Interlock.Ui
{
    /// <summary>Lets a timer tick start a refresh only when the previous one has finished.</summary>
    public sealed class RefreshGate
    {
        private int _running;

        public bool TryEnter() { return Interlocked.CompareExchange(ref _running, 1, 0) == 0; }

        public void Exit() { Interlocked.Exchange(ref _running, 0); }
    }
}

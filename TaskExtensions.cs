using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    // Extension methods must be declared in a non-generic static class
    public static class TaskExtensions
    {
        public static async Task WithCancellation(this Task task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object?>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<object?>)s!).TrySetResult(null), tcs))
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(cancellationToken);
            await task; // propagate exceptions if any
        }
    }
}
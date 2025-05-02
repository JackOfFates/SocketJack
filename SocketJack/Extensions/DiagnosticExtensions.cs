using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Extensions {

    static class DiagnosticExtensions {

        public async static Task<TimeSpan> TimedTask(this Action action) {
            var StartTime = DateTime.UtcNow;
            await Task.Run(action);
            var EndTime = DateTime.UtcNow;
            return EndTime - StartTime;
        }

        public async static Task<TimeSpan> TimedTask(this Action action, CancellationToken CancellationToken) {
            var StartTime = DateTime.UtcNow;
            await Task.Run(action, CancellationToken);
            var EndTime = DateTime.UtcNow;
            return EndTime - StartTime;
        }

        public async static Task<TimedTaskResult<TResult>> TimedTask<TResult>(this Func<TResult> Function) {
            var StartTime = DateTime.UtcNow;
            var Result = await Task.Run(Function);
            var EndTime = DateTime.UtcNow;
            return new TimedTaskResult<TResult>(EndTime - StartTime, Result);
        }

        public async static Task<TimedTaskResult<TResult>> TimedTask<TResult>(this Func<TResult> Function, CancellationToken CancellationToken) {
            var StartTime = DateTime.UtcNow;
            var Result = await Task.Run(Function, CancellationToken);
            var EndTime = DateTime.UtcNow;
            return new TimedTaskResult<TResult>(EndTime - StartTime, Result);
        }
    }

    public class TimedTaskResult<TResult> {

        public TimeSpan ExecutionTime {
            get {
                return _ExecutionTime;
            }
        }
        private TimeSpan _ExecutionTime;

        public TResult Result {
            get {
                return _Result;
            }
        }
        private TResult _Result;

        public TimedTaskResult(TimeSpan ExecutionTime, TResult Result) {
            _ExecutionTime = ExecutionTime;
            _Result = Result;
        }
    }
}
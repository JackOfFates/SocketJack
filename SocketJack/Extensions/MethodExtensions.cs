using System;

namespace SocketJack.Extensions {

    public static class MethodExtensions {

        #region Action

        public class TryActionResult {
            public bool Success { get; private set; }

            public readonly Exception Exception;

            public TryActionResult(Exception Exception) {
                Success = false;
                this.Exception = Exception;
            }

            public TryActionResult() {
                Success = true;
            }
        }

        public static TryActionResult TryInvoke(this Action action) {
            try {
                action.Invoke();
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T>(this Action<T> action, ref T @param) {
            try {
                action.Invoke(@param);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2>(this Action<T, T2> action, ref T @param, ref T2 param2) {
            try {
                action.Invoke(@param, param2);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3>(this Action<T, T2, T3> action, ref T @param, ref T2 param2, ref T3 param3) {
            try {
                action.Invoke(@param, param2, param3);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4>(this Action<T, T2, T3, T4> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4) {
            try {
                action.Invoke(@param, param2, param3, param4);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5>(this Action<T, T2, T3, T4, T5> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5) {
            try {
                action.Invoke(@param, param2, param3, param4, param5);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6>(this Action<T, T2, T3, T4, T5, T6> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, Param6);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7>(this Action<T, T2, T3, T4, T5, T6, T7> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, Param6, Param7);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8>(this Action<T, T2, T3, T4, T5, T6, T7, T8> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7, ref T8 Param8) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, Param6, Param7, Param8);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9> action, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9) {
            try {
                action.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10> action, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10) {
            try {
                action.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> action, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10, ref T11 Param11) {
            try {
                action.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10, Param11);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        public static TryActionResult TryInvoke<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(this Action<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> action, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15, ref T16 param16) {
            try {
                action.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15, param16);
                return new TryActionResult();
            } catch (Exception ex) {
                return new TryActionResult(ex);
            }
        }

        #endregion

        #region Function

        public class TryFuncResult<TResult> {
            public bool Success { get; private set; }

            public readonly Exception Exception;

            public TResult Result { get; private set; }
            public TryFuncResult(Exception Exception) {
                Success = false;
                this.Exception = Exception;
            }

            public TryFuncResult(ref TResult Result) {
                Success = true;
                this.Result = Result;
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult>(this Func<TResult> func) {
            try {
                var Result = func.Invoke();
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T>(this Func<T, TResult> func, ref T @param) {
            try {
                var Result = func.Invoke(@param);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2>(this Func<T, T2, TResult> Func, ref T @param, ref T2 param2) {
            try {
                var Result = Func.Invoke(@param, param2);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3>(this Func<T, T2, T3, TResult> Func, ref T @param, ref T2 param2, ref T3 param3) {
            try {
                var Result = Func.Invoke(@param, param2, param3);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4>(this Func<T, T2, T3, T4, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5>(this Func<T, T2, T3, T4, T5, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6>(this Func<T, T2, T3, T4, T5, T6, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, Param6);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7>(this Func<T, T2, T3, T4, T5, T6, T7, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, Param6, Param7);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8>(this Func<T, T2, T3, T4, T5, T6, T7, T8, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7, ref T8 Param8) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, Param6, Param7, Param8);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, TResult> Func, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9) {
            try {
                var Result = Func.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, TResult> Func, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10) {
            try {
                var Result = Func.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, TResult> Func, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10, ref T11 Param11) {
            try {
                var Result = Func.Invoke(@param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10, Param11);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(this Func<T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, TResult> Func, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15, ref T16 param16) {
            try {
                var Result = Func.Invoke(@param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15, param16);
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        #endregion

        #region Delegate

        public static TryFuncResult<TResult> TryInvoke<TResult>(this Delegate Delegate) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke());
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T>(this Delegate Delegate, ref T @param) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2>(this Delegate Delegate, ref T @param, ref T2 param2) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, Param6 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, Param6, Param7 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 Param6, ref T7 Param7, ref T8 Param8) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, Param6, Param7, Param8 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9>(this Delegate Delegate, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10>(this Delegate Delegate, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(this Delegate Delegate, ref T @param, ref T2 Param2, ref T3 Param3, ref T4 Param4, ref T5 Param5, ref T6 Param6, ref T7 Param7, ref T8 Param8, ref T9 Param9, ref T10 Param10, ref T11 Param11) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, Param2, Param3, Param4, Param5, Param6, Param7, Param8, Param9, Param10, Param11 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        public static TryFuncResult<TResult> TryInvoke<TResult, T, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(this Delegate Delegate, ref T @param, ref T2 param2, ref T3 param3, ref T4 param4, ref T5 param5, ref T6 param6, ref T7 param7, ref T8 param8, ref T9 param9, ref T10 param10, ref T11 param11, ref T12 param12, ref T13 param13, ref T14 param14, ref T15 param15, ref T16 param16) {
            try {
                TResult Result = (TResult)(Delegate.DynamicInvoke(new object[] { @param, param2, param3, param4, param5, param6, param7, param8, param9, param10, param11, param12, param13, param14, param15, param16 }));
                return new TryFuncResult<TResult>(ref Result);
            } catch (Exception ex) {
                return new TryFuncResult<TResult>(ex);
            }
        }

        #endregion

    }
}
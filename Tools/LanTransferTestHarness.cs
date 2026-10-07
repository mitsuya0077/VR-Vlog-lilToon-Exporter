#if VRVLOG_LAN_TRANSFER_CLI
// A small assertion adapter lets the same focused Editor fixtures run in CI
// without distributing NUnit, UnityEngine, or another test runtime. Failures
// never include actual request/QR values or arbitrary exception text.
using System;
using System.Collections;
using System.Reflection;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class TestAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class TestCaseAttribute : Attribute
    {
        public object[] Arguments { get; }
        public TestCaseAttribute(params object[] arguments) { Arguments = arguments; }
    }
    public sealed class Constraint
    {
        private readonly Func<object, bool> predicate;
        internal Constraint(Func<object, bool> predicate) { this.predicate = predicate; }
        internal bool Matches(object value) => predicate(value);
    }
    public static class Is
    {
        public static Constraint True => EqualTo(true);
        public static Constraint False => EqualTo(false);
        public static Constraint EqualTo(object expected) => new Constraint(actual => Equal(actual, expected));
        public static Constraint LessThanOrEqualTo(object expected) => new Constraint(actual => Convert.ToInt64(actual) <= Convert.ToInt64(expected));
        private static bool Equal(object actual, object expected)
        {
            if (actual is IEnumerable left && expected is IEnumerable right && !(actual is string))
            {
                var a = left.GetEnumerator(); var b = right.GetEnumerator();
                try
                {
                    while (true)
                    {
                        var hasA = a.MoveNext(); var hasB = b.MoveNext();
                        if (hasA != hasB) return false;
                        if (!hasA) return true;
                        if (!Equal(a.Current, b.Current)) return false;
                    }
                }
                finally { (a as IDisposable)?.Dispose(); (b as IDisposable)?.Dispose(); }
            }
            return object.Equals(actual, expected);
        }
    }
    public static class Assert
    {
        public static void That<T>(T value, Constraint constraint, string message = null)
        {
            if (!constraint.Matches(value)) throw new InvalidOperationException(message ?? "Assertion failed; actual values are withheld.");
        }
        public static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (Exception exception)
            {
                if (exception.GetType() == typeof(T)) return (T)exception;
                throw new InvalidOperationException("Expected exception type was not observed; exception details are withheld.");
            }
            throw new InvalidOperationException("Expected exception was not observed.");
        }
    }
}

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public static class CloudTransferCliRunner
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            var fixture = new CloudTransferTests();
            foreach (var method in fixture.GetType().GetMethods())
            {
                var cases = method.GetCustomAttributes(typeof(NUnit.Framework.TestCaseAttribute), true);
                if (cases.Length == 0 && method.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), true).Length == 0) continue;
                foreach (var test in cases.Length == 0 ? new object[] { null } : cases)
                    try
                    {
                        var result = method.Invoke(fixture, test == null ? null : ((NUnit.Framework.TestCaseAttribute)test).Arguments);
                        if (result is System.Threading.Tasks.Task task) task.GetAwaiter().GetResult();
                        Console.WriteLine("PASS " + method.Name); passed++;
                    }
                    catch { Console.WriteLine("FAIL " + method.Name + " (failure details withheld)"); failed++; }
            }
            Console.WriteLine("Cloud transfer tests: passed=" + passed + ", failed=" + failed);
            return failed;
        }
    }

    public static class LanTransferCliRunner
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            var fixture = new LanTransferTests();
            foreach (var method in fixture.GetType().GetMethods())
            {
                var cases = method.GetCustomAttributes(typeof(NUnit.Framework.TestCaseAttribute), true);
                if (cases.Length == 0 && method.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), true).Length == 0) continue;
                foreach (var test in cases.Length == 0 ? new object[] { null } : cases)
                {
                    try
                    {
                        method.Invoke(fixture, test == null ? null : ((NUnit.Framework.TestCaseAttribute)test).Arguments);
                        Console.WriteLine("PASS " + method.Name); passed++;
                    }
                    catch
                    {
                        // Method names are fixed source identifiers. Keep generated credentials and
                        // arbitrary exception messages out of console and CI artifacts even on failure.
                        Console.WriteLine("FAIL " + method.Name + " (failure details withheld)"); failed++;
                    }
                }
            }
            Console.WriteLine("LAN transfer tests: passed=" + passed + ", failed=" + failed);
            return failed;
        }
    }
}
#endif

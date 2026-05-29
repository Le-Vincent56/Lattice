using System.Text.RegularExpressions;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Diagnostics
{
    /// <summary>
    /// Verifies the diagnostic dump produced by <see cref="DiagnosticsExtensions.DumpScopeTree"/>:
    /// <list type="bullet">
    ///     <item>identical scope structures yield identical output (modulo per-process scope IDs)</item>
    ///     <item>multi-binding buckets render the implementation count plus per-impl rows; decorator chains annotated</item>
    ///     <item>parent and child scopes both appear in a root-down dump</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public sealed class DumpScopeTreeTests
    {
        /// <summary>
        /// Two containers built with the identical builder configuration must dump
        /// to the same text once per-process scope IDs are normalized. Pins the
        /// ordering invariant that <see cref="Registry.RegistrationOrder"/> provides.
        /// </summary>
        [Test]
        public void DumpScopeTree_WhenSameStructureBuiltTwice_YieldsIdenticalOutput()
        {
            // Local function: build a fresh container with the canonical fixture
            // shape and dump it. Called twice so we can compare the two dumps.
            string DumpOnce()
            {
                using IObjectResolver resolver = Container.Build(builder =>
                {
                    builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                    builder.Register<IServiceB, ServiceB>(Lifetime.Singleton);
                    builder.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                    builder.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
                });
                return resolver.DumpScopeTree();
            }

            // Strip the per-process scope IDs (they monotonically increment over
            // the life of the test process) before comparing. Two passes: one for
            // the "Scope#N" token, one for the "parent=#N" token.
            string Normalize(string raw)
            {
                string step1 = Regex.Replace(raw, @"Scope#\d+", "Scope#N");
                return Regex.Replace(step1, @"parent=#\d+", "parent=#N");
            }

            string firstDump = DumpOnce();
            string secondDump = DumpOnce();

            Assert.AreEqual(Normalize(firstDump), Normalize(secondDump),
                "Identical builder configurations should produce identical dump output once scope IDs are normalized.");
        }

        /// <summary>
        /// A scope containing a multi-binding (three IPipelineStage impls) and a
        /// decorator chain (FirstDecorator wraps ILoggable) must surface both in
        /// the dump output. Asserts substrings rather than exact format so cosmetic
        /// formatting tweaks do not break the test.
        /// </summary>
        [Test]
        public void DumpScopeTree_WhenScopeContainsMultiBindingAndDecorator_DescribesBoth()
        {
            using IObjectResolver resolver = Container.Build(builder =>
            {
                builder.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                builder.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
                builder.Register<IPipelineStage, StageThree>(Lifetime.Transient);
                builder.Register<ILoggable, RealLoggable>(Lifetime.Scoped);
                builder.RegisterDecorator<ILoggable, FirstDecorator>(Lifetime.Scoped);
            });

            string dump = resolver.DumpScopeTree();

            StringAssert.Contains("Root scope", dump);
            StringAssert.Contains("3 implementations", dump);
            StringAssert.Contains("decorator: FirstDecorator wraps ILoggable", dump);
            StringAssert.Contains("StageOne", dump);
            StringAssert.Contains("StageTwo", dump);
            StringAssert.Contains("StageThree", dump);
        }

        /// <summary>
        /// A child scope must appear nested in a dump invoked on the root. Both
        /// scopes' registrations should be visible in the single output string.
        /// </summary>
        [Test]
        public void DumpScopeTree_WhenChildScopeExists_IncludesNestedScope()
        {
            using IObjectResolver root =
                Container.Build(builder => builder.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            using IObjectResolver child =
                root.CreateChildScope(builder => builder.Register<IServiceB, ServiceB>(Lifetime.Scoped));

            string dump = root.DumpScopeTree();

            StringAssert.Contains("Root scope", dump);
            StringAssert.Contains("Child scope", dump);
            StringAssert.Contains("IServiceA", dump);
            StringAssert.Contains("IServiceB", dump);
        }
    }
}
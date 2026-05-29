using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Diagnostics
{
    /// <summary>
    /// Verifies the resolution-path report produced by
    /// <see cref="DiagnosticsExtensions.DumpResolutionPath"/>:
    /// <list type="bullet">
    ///     <item>resolves at the calling scope when the registration is local (hop = 0)</item>
    ///     <item>walks up the parent chain and reports the hop count when only an ancestor has the registration</item>
    ///     <item>emits a "no registration found" message when nothing on the chain matches</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public sealed class DumpResolutionPathTests
    {
        /// <summary>
        /// A registration declared in the calling scope itself must be reported at
        /// hop = 0 with the impl type listed.
        /// </summary>
        [Test]
        public void DumpResolutionPath_WhenRegistrationInCallingScope_ReportsHopZero()
        {
            using IObjectResolver resolver =
                Container.Build(builder => builder.Register<IServiceA, ServiceA>(Lifetime.Singleton));

            string report = resolver.DumpResolutionPath(typeof(IServiceA));

            StringAssert.Contains("hop = 0", report);
            StringAssert.Contains("ServiceA", report);
        }

        /// <summary>
        /// A registration declared on the parent must be found from a child via
        /// the upward walk; hop count should be 1 (one scope above the child).
        /// </summary>
        [Test]
        public void DumpResolutionPath_WhenRegistrationOnlyInParent_ReportsHopOne()
        {
            using IObjectResolver root =
                Container.Build(builder => builder.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            using IObjectResolver child = root.CreateChildScope(_ => { });

            string report = child.DumpResolutionPath(typeof(IServiceA));

            StringAssert.Contains("hop = 1", report);
            StringAssert.Contains("ServiceA", report);
        }

        /// <summary>
        /// A type with no registration anywhere on the ancestor chain must produce
        /// the no-registration message rather than throwing. Diagnostic surface is
        /// query-only; it does not raise on miss.
        /// </summary>
        [Test]
        public void DumpResolutionPath_WhenNoRegistrationAnywhere_ReportsNotFound()
        {
            using IObjectResolver resolver = Container.Build(_ => { });

            string report = resolver.DumpResolutionPath(typeof(IServiceA));

            StringAssert.Contains("no registration found", report);
        }
    }
}
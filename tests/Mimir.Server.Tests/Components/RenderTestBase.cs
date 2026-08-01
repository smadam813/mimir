using Bunit;

namespace Mimir.Server.Tests.Components;

// Is the renderer rather than holding one: BunitContext is public and non-sealed precisely to be a
// test class's base, and a wrapper would forfeit its async dispose path, which xunit.v3 prefers.
public abstract class RenderTestBase : BunitContext;

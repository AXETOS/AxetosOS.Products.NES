using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareProfilerTests
{
    [Fact]
    public void Profiler_is_opt_in_and_samples_direct_package_and_net_work()
    {
        var board = new VirtualHardwareBoard("profile-probe");
        var source = board.Add(new DigitalOscillator("source", 1));
        var probe = board.Add(new ProfileProbe("probe"));
        board.Connect("signal", source.Output, probe.Input);
        var simulator = new VirtualHardwareSimulator(board);

        for (var index = 0; index < 32; index++)
            source.AdvanceHalfCycle();

        Assert.Equal(0, probe.ProfiledEvaluationCount);

        simulator.SetProfilingEnabled(true);
        for (var index = 0; index < 640; index++)
            source.AdvanceHalfCycle();

        var profile = simulator.GetProfileSnapshot();
        var component = Assert.Single(profile.Components, item => item.ComponentId == "probe");

        Assert.True(component.EvaluationCount >= 600);
        Assert.True(component.TimedEvaluationCount >= 2);
        Assert.True(probe.ProfiledEvaluationCount >= 2);
        Assert.True(profile.NetResolutionAttempts >= 600);
        Assert.True(profile.TimedNetResolutionSamples >= 2);
        Assert.Contains(
            profile.Sections,
            section => section.ComponentId == "probe"
                && section.Section == nameof(VirtualHardwareProfileSection.Rp2A03CpuCore)
                && section.SampleCount >= 2);
    }

    private sealed class ProfileProbe : VirtualHardwareComponent
    {
        public ProfileProbe(string componentId) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input);
        }

        public DigitalPin Input { get; }
        public int EvaluationCount { get; private set; }
        public int ProfiledEvaluationCount { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask) => EvaluationCount++;

        protected override void OnInputChangesProfiled(
            ulong changedInputMask,
            VirtualHardwareProfileSample sample)
        {
            var started = sample.BeginSection();
            EvaluationCount++;
            ProfiledEvaluationCount++;
            sample.EndSection(VirtualHardwareProfileSection.Rp2A03CpuCore, started);
        }
    }
}

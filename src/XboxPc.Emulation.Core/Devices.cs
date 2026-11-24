namespace XboxPc.Emulation.Core;

/// <summary>Interrupt controller as seen by devices: assert an IRQ line (Xbox: 8259-style lines 0-15).</summary>
public interface IInterruptController
{
    void RaiseIrq(int line);
}

/// <summary>A device with time-based behaviour (vertical blank, USB frames, audio DMA), driven by virtual CPU cycles.</summary>
public interface IClockedDevice
{
    /// <summary>Virtual cycle at which <see cref="OnEvent"/> must be called; long.MaxValue when idle.</summary>
    long NextEventCycle { get; }

    void OnEvent(long nowCycles);
}

/// <summary>Receives reports of guest accesses to hardware state that is not modelled (shown in run reports).</summary>
public interface IHardwareReport
{
    void Unmodelled(string message);
}

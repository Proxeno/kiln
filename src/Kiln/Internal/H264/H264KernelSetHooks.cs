namespace Kiln.Internal.H264;

/// <summary>
/// The single install point for measurement-only kernel-set decorators.
///
/// <para>
/// A benchmark harness (for example the stage profiler's counting pass) can set
/// <see cref="MeasurementDecorator"/> to wrap whatever set an encoder resolves. The hook is read
/// once per encoder, when the set is built; it is <see langword="null"/> in normal operation, so
/// the default path returns exactly the set the encoder would have built and is bit-identical.
/// </para>
/// </summary>
internal static class H264KernelSetHooks
{
    /// <summary>
    /// Measurement-only decorator applied by <see cref="Decorate"/> when an encoder builds its
    /// kernel set. <see langword="null"/> (the default) leaves the set untouched. Callers are
    /// responsible for clearing it after their measurement run.
    /// </summary>
    internal static Func<IH264KernelSet, IH264KernelSet>? MeasurementDecorator { get; set; }

    /// <summary>Applies <see cref="MeasurementDecorator"/>, or returns <paramref name="kernels"/> unchanged when unset.</summary>
    internal static IH264KernelSet Decorate(IH264KernelSet kernels)
    {
        var decorator = MeasurementDecorator;
        return decorator is null ? kernels : decorator(kernels);
    }
}

using System.Reflection;

namespace S880Ctl;

internal sealed record SpeakerTargetBinding(string AddressText, ulong Address, bool IsConfigured)
{
    public static SpeakerTargetBinding Unconfigured { get; } = new("", 0, false);
    public static SpeakerTargetBinding Current { get; } = ReadBuildBinding();

    internal static SpeakerTargetBinding Create(string addressText)
    {
        if (!BluetoothAddress.TryParse(addressText, out var address) || address == 0)
            throw new ArgumentException("A configured speaker address must be a nonzero 48-bit Bluetooth address.", nameof(addressText));
        return new(BluetoothAddress.Format(address), address, true);
    }

    private static SpeakerTargetBinding ReadBuildBinding()
    {
        var value = typeof(SpeakerTargetBinding).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "SpeakerAddress")
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? Unconfigured : Create(value);
    }
}

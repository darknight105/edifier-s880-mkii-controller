using System;
using System.Linq;
using System.Reflection;

namespace S880Tray;

internal static class BuildSpeakerBinding
{
    internal static string Address { get; } = ReadAddress();
    internal static bool IsConfigured => Address.Length != 0;

    private static string ReadAddress()
    {
        var value = typeof(BuildSpeakerBinding).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "SpeakerAddress")
            ?.Value;
        return value ?? "";
    }
}

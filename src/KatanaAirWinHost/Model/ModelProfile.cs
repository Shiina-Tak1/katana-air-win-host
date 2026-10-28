namespace KatanaAirWinHost.Model;

/// <summary>Per-model BLE and frontend settings.</summary>
internal sealed record ModelProfile(
    string Id,
    string DisplayName,
    string FrontendName,
    string BleNamePrefix,
    string BleDeviceName,
    string EndpointKey,
    string EndpointUid,
    Guid MidiServiceUuid,
    Guid MidiCharacteristicUuid);

internal static class ModelProfiles
{
    public static readonly ModelProfile KatanaAir = new(
        "katana-air",
        "KATANA-AIR",
        "BOSS TONE STUDIO for KATANA-AIR",
        "KATANA-AIR",
        "KATANA-AIR MIDI",
        "KATANA-AIR-BLE",
        "KATANA-AIR-BLE",
        Guid.Parse("03B80E5A-EDE8-4B33-A751-6CE34EC4C700"),
        Guid.Parse("7772E5DB-3868-4112-A1A9-F2669D106BF3"));

    // Same frontend and BLE name as KatanaAir; KATANA-AIR EX hardware not tested.
    public static readonly ModelProfile KatanaAirEx = new(
        "katana-air-ex",
        "KATANA-AIR EX",
        KatanaAir.FrontendName,
        KatanaAir.BleNamePrefix,
        KatanaAir.BleDeviceName,
        KatanaAir.EndpointKey,
        KatanaAir.EndpointUid,
        KatanaAir.MidiServiceUuid,
        KatanaAir.MidiCharacteristicUuid);

    public static readonly IReadOnlyList<ModelProfile> All = new[] { KatanaAir, KatanaAirEx };

    public static ModelProfile Default => KatanaAir;

    public static ModelProfile? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

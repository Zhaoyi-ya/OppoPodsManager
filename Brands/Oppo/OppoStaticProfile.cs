using System.Text.Json;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Static Melody whitelist profile. Runtime effective capabilities are produced by
/// intersecting this with the device command bitmap.
/// </summary>
public sealed record OppoStaticProfile(
    string ProductId,
    string ModelName,
    IReadOnlySet<DeviceFeature> Features,
    bool SupportsCustomEqualizer,
    bool SupportsMultiDevice,
    int? EqualizerBandCount)
{
    public static OppoStaticProfile FromWhitelistEntry(string productId, JsonElement entry)
    {
        var name = entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? productId
            : productId;

        var features = new HashSet<DeviceFeature> { DeviceFeature.Battery };
        var supportsCustomEq = false;
        var supportsMultiDevice = false;
        int? eqBands = null;

        if (entry.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (HasNoiseModes(function))
                features.Add(DeviceFeature.Anc);

            if (HasEqualizer(function, out supportsCustomEq, out eqBands))
                features.Add(DeviceFeature.Equalizer);

            if (HasSpatial(function))
                features.Add(DeviceFeature.SpatialAudio);

            if (HasGaming(function))
                features.Add(DeviceFeature.Gaming);

            if (HasMultiDevice(function, out supportsMultiDevice))
            {
                features.Add(DeviceFeature.MultiDevice);
                features.Add(DeviceFeature.DualDevice);
            }

            if (FlagOn(function, "findDevice") || FlagOn(function, "FindDevice"))
                features.Add(DeviceFeature.FindDevice);

            if (FlagOn(function, "keyFunction") || FlagOn(function, "clickTakePic") || FlagOn(function, "clickTakePicNew"))
                features.Add(DeviceFeature.TouchControls);

            if (FlagOn(function, "autoFirmwareUpdate") || FlagOn(function, "firmwareUpdate"))
                features.Add(DeviceFeature.FirmwareUpdate);

            if (FlagOn(function, "dualDevice") || FlagOn(function, "multiConnect"))
                features.Add(DeviceFeature.DualDevice);

            if (HasGameSound(function))
                features.Add(DeviceFeature.GameSound);

            if (FlagOn(function, "bassEngine"))
                features.Add(DeviceFeature.BassEngine);

            if (FlagOn(function, "vocalEnhance"))
                features.Add(DeviceFeature.VocalEnhance);

            if (FlagOn(function, "hearingEnhancement") || FlagOn(function, "hearingEnhance"))
                features.Add(DeviceFeature.HearingEnhance);

            if (FlagOn(function, "longPowerMode"))
                features.Add(DeviceFeature.LongPowerMode);

            if (FlagOn(function, "wearDetection"))
                features.Add(DeviceFeature.WearDetection);

            if (FlagOn(function, "spineHealth") || FlagOn(function, "spineLiveMonitor"))
                features.Add(DeviceFeature.SpineHealth);
        }

        return new OppoStaticProfile(
            productId,
            name,
            features,
            supportsCustomEq,
            supportsMultiDevice,
            eqBands);
    }

    private static bool HasNoiseModes(JsonElement function) =>
        function.TryGetProperty("noiseReductionMode", out var nrm)
        && nrm.ValueKind == JsonValueKind.Array
        && nrm.GetArrayLength() > 0;

    private static bool HasEqualizer(
        JsonElement function,
        out bool customEq,
        out int? bands)
    {
        customEq = FlagOn(function, "customEqualizer");
        bands = null;
        if (function.TryGetProperty("customEqFrequency", out var freq)
            && freq.ValueKind == JsonValueKind.Array
            && freq.GetArrayLength() > 0)
        {
            bands = freq.GetArrayLength();
            customEq = true;
        }

        var hasBuiltin = function.TryGetProperty("equalizerMode", out var modes)
            && modes.ValueKind == JsonValueKind.Array
            && modes.GetArrayLength() > 0;
        return customEq || hasBuiltin;
    }

    private static bool HasSpatial(JsonElement function)
    {
        if (function.TryGetProperty("spatialTypes", out var spatial)
            && spatial.ValueKind == JsonValueKind.Array
            && spatial.GetArrayLength() > 0)
            return true;

        return FlagOn(function, "spatialAudio") || FlagOn(function, "spatialSound");
    }

    private static bool HasGaming(JsonElement function)
    {
        if (FlagOn(function, "gameMode") || FlagOn(function, "gameModeList"))
            return true;
        if (function.TryGetProperty("gameSoundList", out var list)
            && list.ValueKind == JsonValueKind.Array
            && list.GetArrayLength() > 0)
            return true;
        return FlagOn(function, "gameSound");
    }

    private static bool HasGameSound(JsonElement function)
    {
        if (function.TryGetProperty("gameSoundList", out var list)
            && list.ValueKind == JsonValueKind.Array
            && list.GetArrayLength() > 0)
            return true;
        return FlagOn(function, "gameSound");
    }

    private static bool HasMultiDevice(JsonElement function, out bool manage)
    {
        manage = false;
        if (function.TryGetProperty("multiDevicesConnect", out var mdc)
            && mdc.ValueKind == JsonValueKind.Number
            && mdc.TryGetInt32(out var version)
            && version >= 1)
        {
            manage = version >= 2;
            return true;
        }

        if (function.TryGetProperty("multiConnectFunctions", out var mcf)
            && mcf.ValueKind == JsonValueKind.Array
            && mcf.GetArrayLength() > 0)
        {
            foreach (var item in mcf.EnumerateArray())
            {
                if (item.TryGetProperty("functionType", out var ft)
                    && ft.ValueKind == JsonValueKind.String
                    && string.Equals(ft.GetString(), "unpairDevice", StringComparison.OrdinalIgnoreCase))
                {
                    manage = true;
                    break;
                }
            }

            return true;
        }

        return FlagOn(function, "multiConnect");
    }

    private static bool FlagOn(JsonElement function, string key)
    {
        if (!function.TryGetProperty(key, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n >= 1,
            JsonValueKind.Object => true,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => false,
        };
    }
}

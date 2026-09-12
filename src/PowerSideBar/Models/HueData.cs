using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PowerSideBar.Models;

// ── Generic CLIP v2 response wrapper ─────────────────────────────────────────

public class HueApiResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<HueApiError> Errors { get; set; } = new();
}

public class HueApiError
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

// ── Light ─────────────────────────────────────────────────────────────────────

public class HueLight
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public HueLightMetadata Metadata { get; set; } = new();

    [JsonPropertyName("on")]
    public HueOnState? On { get; set; }

    [JsonPropertyName("dimming")]
    public HueDimming? Dimming { get; set; }

    [JsonPropertyName("color_temperature")]
    public HueColorTemperature? ColorTemperature { get; set; }

    [JsonPropertyName("color")]
    public HueColor? Color { get; set; }
}

public class HueLightMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("archetype")]
    public string Archetype { get; set; } = string.Empty;
}

public class HueOnState
{
    [JsonPropertyName("on")]
    public bool On { get; set; }
}

public class HueDimming
{
    [JsonPropertyName("brightness")]
    public double Brightness { get; set; }
}

public class HueColorTemperature
{
    [JsonPropertyName("mirek")]
    public int? Mirek { get; set; }

    [JsonPropertyName("mirek_schema")]
    public HueMirekSchema? MirekSchema { get; set; }
}

public class HueMirekSchema
{
    [JsonPropertyName("mirek_minimum")]
    public int MirekMinimum { get; set; } = 153;

    [JsonPropertyName("mirek_maximum")]
    public int MirekMaximum { get; set; } = 500;
}

public class HueColor
{
    [JsonPropertyName("xy")]
    public HueColorXY? Xy { get; set; }
}

public class HueColorXY
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

// ── Room ──────────────────────────────────────────────────────────────────────

public class HueRoom
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public HueRoomMetadata Metadata { get; set; } = new();

    [JsonPropertyName("children")]
    public List<HueResourceLink> Children { get; set; } = new();

    [JsonPropertyName("services")]
    public List<HueResourceLink> Services { get; set; } = new();
}

public class HueRoomMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("archetype")]
    public string Archetype { get; set; } = string.Empty;
}

// ── Scene ─────────────────────────────────────────────────────────────────────

public class HueScene
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public HueSceneMetadata Metadata { get; set; } = new();

    [JsonPropertyName("group")]
    public HueResourceLink? Group { get; set; }
}

public class HueSceneMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class HueResourceLink
{
    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("rtype")]
    public string Rtype { get; set; } = string.Empty;
}

// ── Grouped light (room-level toggle) ─────────────────────────────────────────

public class HueGroupedLight
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("on")]
    public HueOnState? On { get; set; }
}

// ── Bridge discovery (discovery.meethue.com) ─────────────────────────────────

public class HueBridgeInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("internalipaddress")]
    public string InternalIpAddress { get; set; } = string.Empty;
}

// ── Pairing response (legacy /api endpoint) ──────────────────────────────────

public class HuePairResult
{
    [JsonPropertyName("success")]
    public HuePairSuccess? Success { get; set; }

    [JsonPropertyName("error")]
    public HuePairError? Error { get; set; }
}

public class HuePairSuccess
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

public class HuePairError
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

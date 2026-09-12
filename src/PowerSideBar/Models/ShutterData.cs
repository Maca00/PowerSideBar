namespace PowerSideBar.Models;

/// <summary>A single shutter/blind device registered on the Dooya SHC controller.</summary>
public record ShutterDevice(
    string Id,      // raw hex identifier, e.g. "01,01,01,63"
    int Channel,    // channel number
    string Name,    // display name
    int Position    // last known position 0-100 (0 = closed, 100 = open)
);

public enum ShutterCommand { Up, Down, Stop }

public enum ShutterConnectionState { Idle, Connecting, Connected, Error }

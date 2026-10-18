IMyRadioAntenna radio;
IMyRemoteControl remote;
int remaining;
string observed;

public Program()
{
    var antennas = new List<IMyRadioAntenna>();
    GridTerminalSystem.GetBlocksOfType(antennas);
    radio = antennas[0];
    var controls = new List<IMyRemoteControl>();
    GridTerminalSystem.GetBlocksOfType(controls);
    remote = controls[0];
}

public void Main(string argument, UpdateType updateSource)
{
    if (argument == "drop")
    {
        radio.Radius = 1;
        remaining = 5;
        observed = "unknown";
        Runtime.UpdateFrequency = UpdateFrequency.Update100;
        Echo("Temporary radio outage; automatic restoration in about 8 seconds.");
    }
    else if (remaining > 0)
    {
        if (remaining == 3) observed = remote.IsAutoPilotEnabled ? "on" : "off";
        if (--remaining == 0)
        {
            radio.Radius = 2500;
            Runtime.UpdateFrequency = UpdateFrequency.None;
            Echo("autopilot_during_outage=" + observed);
        }
    }
}

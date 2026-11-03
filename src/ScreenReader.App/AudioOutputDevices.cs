using NAudio.CoreAudioApi;

namespace ScreenReader.App;

public sealed record AudioOutputChoice(string Id,string Name,bool Available=true);
public static class AudioOutputDevices
{
    public static IReadOnlyList<AudioOutputChoice> List()
    {
        var result=new List<AudioOutputChoice> { new("",UiText.T("audio.default")) };
        using var enumerator=new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
            using (device) result.Add(new(device.ID,device.FriendlyName));
        return result;
    }
    public static MMDevice Resolve(string deviceId)
    {
        using var enumerator=new MMDeviceEnumerator();
        try
        {
            var device=string.IsNullOrWhiteSpace(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia) : enumerator.GetDevice(deviceId);
            if (device.State==DeviceState.Active) return device;
            device.Dispose(); throw new InvalidOperationException();
        }
        catch (Exception e) { throw new InvalidOperationException(UiText.T(string.IsNullOrWhiteSpace(deviceId) ? "audio.none" : "audio.missing"),e); }
    }
}

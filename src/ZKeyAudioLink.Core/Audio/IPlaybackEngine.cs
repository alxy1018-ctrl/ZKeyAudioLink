using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>The narrow surface PlaybackController needs, so its races can be tested without devices.</summary>
public interface IPlaybackEngine
{
    event Action<MaterialCompletion>? MaterialCompleted;

    Task PlayMaterialAsync(MediaEntry entry, int generation, CancellationToken cancellationToken);

    void StopMaterial(int generation);
}

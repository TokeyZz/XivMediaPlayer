using System.Numerics;

namespace MediaPlayerCore {
  public interface IMediaGameObject {
    public string Name { get; }
    public Vector3 Position { get; }
    public Vector3 Rotation { get; }
    public Vector3 Forward { get; }
    public Vector3 Top { get; }
    public string FocusedPlayerObject { get; }
    bool Invalid { get; }
  }
}

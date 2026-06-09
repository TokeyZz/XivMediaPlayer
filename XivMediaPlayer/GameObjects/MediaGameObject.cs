using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Numerics;
using IMediaGameObject = MediaPlayerCore.IMediaGameObject;

namespace XivMediaPlayer.GameObjects {
  public unsafe class MediaGameObject : IMediaGameObject {
    private string _name = "";
    private Vector3 _position = new Vector3();
    private Vector3 _rotation = new Vector3();
    private string _focusedPlayerObject = "";
    private Vector3 _forward = new Vector3();
    private Vector3 _top = new Vector3(0, 1, 0);

    public string Name => _name;
    public Vector3 Position => _position;
    public Vector3 Rotation => _rotation;
    public string FocusedPlayerObject => _focusedPlayerObject;
    public Vector3 Forward => _forward;
    public Vector3 Top => _top;

    bool IMediaGameObject.Invalid => false;

    public MediaGameObject(string name, Vector3 position) {
      _name = name;
      _position = position;
    }

    public void SetPosition(Vector3 position) {
      _position = position;
    }

    public void Update() {
      // Satisfies IMediaGameObject interface, actual update handled in Plugin.cs
    }

    public void Update(IGameObject gameObject) {
      if (gameObject != null) {
        try {
          if (!string.IsNullOrEmpty(gameObject.Name.TextValue))
              _name = gameObject.Name.TextValue;
          
          _position = gameObject.Position;
          float rot = gameObject.Rotation;
          _rotation = new Vector3(0, rot, 0);
          _forward = new Vector3((float)Math.Cos(rot), 0, (float)Math.Sin(rot));
          
          if (gameObject.TargetObject != null && gameObject.TargetObject.ObjectKind == ObjectKind.Pc) {
              _focusedPlayerObject = gameObject.TargetObject.Name.TextValue;
          } else {
              _focusedPlayerObject = "";
          }
        } catch {
        }
      }
    }
  }
}

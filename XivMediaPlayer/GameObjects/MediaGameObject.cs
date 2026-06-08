using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Numerics;
using IMediaGameObject = MediaPlayerCore.IMediaGameObject;

namespace XivMediaPlayer.GameObjects {
  public unsafe class MediaGameObject : IMediaGameObject {
    private IGameObject _gameObject;
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

    public IGameObject GameObject => _gameObject;

    bool IMediaGameObject.Invalid => false;

    public MediaGameObject(IGameObject gameObject) {
      _gameObject = gameObject;
      Update();
    }

    public MediaGameObject(string name, Vector3 position) {
      _name = name;
      _position = position;
    }

    public MediaGameObject(IGameObject gameObject, string name, Vector3 position) {
      _gameObject = gameObject;
      _name = name;
      _position = position;
      Update();
    }

    public void SetPosition(Vector3 position) {
      _position = position;
    }

    public void Update() {
      if (_gameObject != null) {
        try {
          if (!string.IsNullOrEmpty(_gameObject.Name.TextValue))
              _name = _gameObject.Name.TextValue;
          
          _position = _gameObject.Position;
          float rot = _gameObject.Rotation;
          _rotation = new Vector3(0, rot, 0);
          _forward = new Vector3((float)Math.Cos(rot), 0, (float)Math.Sin(rot));
          
          if (_gameObject.TargetObject != null && _gameObject.TargetObject.ObjectKind == ObjectKind.Pc) {
              _focusedPlayerObject = _gameObject.TargetObject.Name.TextValue;
          } else {
              _focusedPlayerObject = "";
          }
        } catch {
        }
      }
    }
  }
}

using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Numerics;
using IMediaGameObject = MediaPlayerCore.IMediaGameObject;

namespace XivMediaPlayer.GameObjects {
  public unsafe class MediaGameObject : IMediaGameObject {
    IGameObject _gameObject;
    private string _name = "";
    private Vector3 _position = new Vector3();

    string IMediaGameObject.Name {
      get {
        try {
          return (_gameObject != null ? _gameObject.Name.TextValue : _name);
        } catch {
          return _name;
        }
      }
    }

    Vector3 IMediaGameObject.Position {
      get {
        try {
          return (_gameObject != null ? _gameObject.Position : _position);
        } catch {
          return _position;
        }
      }
    }

    Vector3 IMediaGameObject.Rotation {
      get {
        try {
          return new Vector3(0, _gameObject != null ? _gameObject.Rotation : 0, 0);
        } catch {
          return new Vector3(0, 0, 0);
        }
      }
    }

    string IMediaGameObject.FocusedPlayerObject {
      get {
        if (_gameObject != null) {
          try {
            return _gameObject.TargetObject != null ?
              (_gameObject.TargetObject.ObjectKind == ObjectKind.Pc ? _gameObject.TargetObject.Name.TextValue : "")
              : "";
          } catch {
            return "";
          }
        } else {
          return "";
        }
      }
    }

    Vector3 IMediaGameObject.Forward {
      get {
        float rotation = _gameObject != null ? _gameObject.Rotation : 0;
        return new Vector3((float)Math.Cos(rotation), 0, (float)Math.Sin(rotation));
      }
    }

    public Vector3 Top {
      get {
        return new Vector3(0, 1, 0);
      }
    }

    public IGameObject GameObject => _gameObject;

    bool IMediaGameObject.Invalid => false;

    public MediaGameObject(IGameObject gameObject) {
      _gameObject = gameObject;
    }

    public MediaGameObject(string name, Vector3 position) {
      _name = name;
      _position = position;
    }

    public MediaGameObject(IGameObject gameObject, string name, Vector3 position) {
      _gameObject = gameObject;
      _name = name;
      _position = position;
    }
  }
}

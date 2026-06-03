using FFXIVClientStructs.FFXIV.Client.Game;
using MediaPlayerCore;
using System;
using System.Numerics;

namespace XivMediaPlayer.GameObjects {
  public class MediaCameraObject : IMediaGameObject {
    private unsafe Camera* _camera;

    public unsafe MediaCameraObject(Camera* camera) {
      this._camera = camera;
    }

    public string Name => "Camera";

    unsafe public Vector3 Position {
      get {
        return _camera->CameraBase.SceneCamera.Object.Position;
      }
    }

    unsafe public Vector3 Rotation {
      get {
        return Q2E(Quaternion.CreateFromRotationMatrix(_camera->CameraBase.SceneCamera.ViewMatrix));
      }
    }

    unsafe public Vector3 Forward {
      get {
        var cameraViewMatrix = _camera->CameraBase.SceneCamera.ViewMatrix;
        return new Vector3(cameraViewMatrix.M13, cameraViewMatrix.M23, cameraViewMatrix.M33);
      }
    }

    unsafe public Vector3 Top {
      get {
        return _camera->CameraBase.SceneCamera.Vector_1; ;
      }
    }

    public string FocusedPlayerObject {
      get {
        return "";
      }
    }

    bool IMediaGameObject.Invalid => false;

    public static Vector3 Q2E(Quaternion q) {
      Vector3 angles;
      angles.X = (float)Math.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
      if (Math.Abs(2 * (q.W * q.Y - q.Z * q.X)) >= 1) angles.Y = (float)Math.CopySign(Math.PI / 2, 2 * (q.W * q.Y - q.Z * q.X));
      else angles.Y = (float)Math.Asin(2 * (q.W * q.Y - q.Z * q.X));
      angles.Z = (float)Math.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));

      return new Vector3() {
        X = (float)(180 / Math.PI) * angles.X,
        Y = (float)(180 / Math.PI) * angles.Y,
        Z = (float)(180 / Math.PI) * angles.Z
      };
    }
  }
}

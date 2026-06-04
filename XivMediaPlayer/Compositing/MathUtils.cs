using System.Numerics;

namespace XivMediaPlayer.Compositing {
  internal static class MathUtils {
    /// <summary>
    /// Unprojects a 2D screen coordinate into a 3D ray in world space.
    /// </summary>
    public static bool ScreenPointToRay(Vector2 screenPos, int screenWidth, int screenHeight, Matrix4x4 viewProj, out Vector3 rayOrigin, out Vector3 rayDirection) {
      rayOrigin = Vector3.Zero;
      rayDirection = Vector3.Zero;

      if (!Matrix4x4.Invert(viewProj, out var invViewProj)) return false;

      // Convert screen coordinate to Normalized Device Coordinates (-1 to +1)
      float nx = (screenPos.X / screenWidth) * 2.0f - 1.0f;
      float ny = -((screenPos.Y / screenHeight) * 2.0f - 1.0f);

      // In FFXIV reverse-Z, near plane is 1.0, far plane is 0.0
      var nearP4 = Vector4.Transform(new Vector4(nx, ny, 1.0f, 1.0f), invViewProj);
      var nearPoint = new Vector3(nearP4.X / nearP4.W, nearP4.Y / nearP4.W, nearP4.Z / nearP4.W);

      var farP4 = Vector4.Transform(new Vector4(nx, ny, 0.0f, 1.0f), invViewProj);
      var farPoint = new Vector3(farP4.X / farP4.W, farP4.Y / farP4.W, farP4.Z / farP4.W);

      rayOrigin = nearPoint;
      rayDirection = Vector3.Normalize(farPoint - nearPoint);

      return true;
    }

    /// <summary>
    /// Intersects a ray with a 3D quad and computes the UV coordinates if it hits.
    /// </summary>
    public static bool RayQuadIntersect(Vector3 rayOrigin, Vector3 rayDir, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, out Vector2 uv) {
      uv = Vector2.Zero;

      // The quad might not be perfectly planar, so we split it into two triangles: TL-TR-BR and TL-BR-BL
      if (RayTriangleIntersect(rayOrigin, rayDir, tl, tr, br, out float u1, out float v1)) {
        // Correct UV mapping for Top-Right triangle
        uv = new Vector2(u1 + v1, v1);
        return true;
      }

      if (RayTriangleIntersect(rayOrigin, rayDir, tl, br, bl, out float u2, out float v2)) {
        // Bottom-left triangle: UVs map TL(0,0), BR(1,1), BL(0,1)
        uv = new Vector2(v2, u2 + v2);
        return true;
      }

      return false;
    }

    /// <summary>
    /// Möller–Trumbore intersection algorithm.
    /// </summary>
    private static bool RayTriangleIntersect(Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float u, out float v) {
      u = 0; v = 0;
      float t = 0;

      Vector3 edge1 = v1 - v0;
      Vector3 edge2 = v2 - v0;
      Vector3 h = Vector3.Cross(dir, edge2);
      float a = Vector3.Dot(edge1, h);

      if (a > -0.00001f && a < 0.00001f) return false;

      float f = 1.0f / a;
      Vector3 s = orig - v0;
      u = f * Vector3.Dot(s, h);

      if (u < 0.0f || u > 1.0f) return false;

      Vector3 q = Vector3.Cross(s, edge1);
      v = f * Vector3.Dot(dir, q);

      if (v < 0.0f || u + v > 1.0f) return false;

      t = f * Vector3.Dot(edge2, q);
      return t > 0.00001f;
    }

    /// <summary>
    /// Computes the 2D cross product of two vectors.
    /// </summary>
    public static float Cross2D(Vector2 a, Vector2 b) {
      return a.X * b.Y - a.Y * b.X;
    }

    /// <summary>
    /// Computes the UV coordinates of a point within a 2D arbitrary quadrilateral.
    /// </summary>
    public static Vector2 InverseBilinear(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d) {
      Vector2 e = b - a;
      Vector2 f = d - a;
      Vector2 g = a - b + c - d;
      Vector2 h = p - a;

      float k2 = Cross2D(g, f);
      float k1 = Cross2D(e, f) + Cross2D(h, g);
      float k0 = Cross2D(h, e);

      float w = k1 * k1 - 4.0f * k0 * k2;
      if (w < 0.0f) return new Vector2(-1, -1);

      w = (float)System.Math.Sqrt(w);
      float v1 = (-k1 - w) / (2.0f * k2);
      float v2 = (-k1 + w) / (2.0f * k2);
      float v = (v1 >= 0.0f && v1 <= 1.0f) ? v1 : v2;

      float denominatorX = e.X + g.X * v;
      float u = 0;
      if (System.Math.Abs(denominatorX) > 0.0001f) {
        u = (h.X - f.X * v) / denominatorX;
      } else {
        float denominatorY = e.Y + g.Y * v;
        if (System.Math.Abs(denominatorY) > 0.0001f) {
           u = (h.Y - f.Y * v) / denominatorY;
        }
      }

      return new Vector2(u, v);
    }
  }
}

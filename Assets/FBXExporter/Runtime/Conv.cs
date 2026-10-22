using System;
using UnityEngine;

namespace FbxExporter
{
    /// <summary>Unity (left-handed, Y-up) to FBX (right-handed, Y-up) conversion by mirroring X.</summary>
    internal static class Conv
    {
        public static Vector3 Position(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        public static Quaternion Rotation(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        /// <summary>FBX eEulerXYZ angles in degrees (R = Rz * Ry * Rx) for an already converted quaternion.</summary>
        public static (double x, double y, double z) EulerXYZ(Quaternion q)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            double len = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-12) return (0, 0, 0);
            x /= len; y /= len; z /= len; w /= len;

            double m00 = 1 - 2 * (y * y + z * z);
            double m10 = 2 * (x * y + z * w);
            double m11 = 1 - 2 * (x * x + z * z);
            double m12 = 2 * (y * z - x * w);
            double m20 = 2 * (x * z - y * w);
            double m21 = 2 * (y * z + x * w);
            double m22 = 1 - 2 * (x * x + y * y);

            double sy = Math.Max(-1.0, Math.Min(1.0, -m20));
            double rx, ry, rz;
            if (Math.Abs(sy) < 0.9999999)
            {
                ry = Math.Asin(sy);
                rx = Math.Atan2(m21, m22);
                rz = Math.Atan2(m10, m00);
            }
            else
            {
                // Gimbal lock: fold Z into X.
                ry = sy > 0 ? Math.PI / 2 : -Math.PI / 2;
                rz = 0;
                rx = Math.Atan2(-m12, m11);
            }
            const double Rad2Deg = 180.0 / Math.PI;
            return (rx * Rad2Deg, ry * Rad2Deg, rz * Rad2Deg);
        }

        /// <summary>S * M * S with S = diag(-1, 1, 1, 1).</summary>
        public static Matrix4x4 Matrix(Matrix4x4 m)
        {
            m.m01 = -m.m01; m.m02 = -m.m02; m.m03 = -m.m03;
            m.m10 = -m.m10; m.m20 = -m.m20; m.m30 = -m.m30;
            return m;
        }

        /// <summary>Converted matrix as the 16 column-major doubles FBX expects (translation in elements 12..14).</summary>
        public static double[] MatrixArray(Matrix4x4 unityMatrix)
        {
            var m = Matrix(unityMatrix);
            var a = new double[16];
            for (int i = 0; i < 16; i++) a[i] = m[i]; // Unity's linear indexer is column-major
            return a;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DroneMonitor
{
    internal class Estimate
    {
        public float[] RPY { get; set; } = new float[3];
        public float[] quate { get; set; } = new float[4];
        public float[] accel { get; set; } = new float[3];
        public float[] gyro { get; set; } = new float[3];

        public float dt { get; set; } = 4E-3f;

        //xhat = [px, py, pz, vx, vy, vz, bx, by, bz]
        //y = [ax, ay, az, q0, q1, q2, q3]

        public float[] xhat { get; set; } = new float[9];
        public float[] A { get; set; } = new float[9];



        public void UpdateEstimate(Estimate estimate, float[] pry, float[] accel, float[] gyro, float[] quate)
        {
            if (pry.Length < 3 || accel.Length < 3 || gyro.Length < 3)
            {
                throw new ArgumentException("Data length must be at least 24 bytes.");
            }
            for (int i = 0; i < 3; i++)
            {
                estimate.RPY[i] = pry[i];
                estimate.accel[i] = accel[i];
                estimate.gyro[i] = gyro[i];
            }
            for (int i = 0; i < 4; i++)
            {
                estimate.quate[i] = quate[i];
            }
        }

        public void calcEKF()
        {

        }
    }
}

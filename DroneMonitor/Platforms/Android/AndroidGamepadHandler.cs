using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.Views;
using Android.App;
using Android.Runtime;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Controls;
using Android.Content;
using Android.Hardware.Input;
using Android.Views.InputMethods;
using System;
using System.Collections.Generic;

namespace DroneMonitor.Platforms.Android
{
    public class AndroidGamepadHandler : Java.Lang.Object, global::Android.Views.View.IOnKeyListener, IDisposable
    {
        readonly global::Android.Views.View _targetView;
        readonly Label _msgPad;

        public bool IsControlByPad { get; private set; } = false;

        // U5[0]=Throttle, U5[1..4]=Roll/L-R, Pitch/U-D, etc.
        public List<float> U5 { get; } = new List<float> { 0f, 0f, 0f, 0f, 0f };

        const float ROLL_GAIN = 30f;
        const float PITCH_GAIN = 30f;
        const float YAW_GAIN = 10f;

        public AndroidGamepadHandler(global::Android.Views.View targetView)
        {
            _targetView = targetView;

            // フォーカスを許可してリスナー登録
            _targetView.Focusable = true;
            _targetView.FocusableInTouchMode = true;
            _targetView.RequestFocus();
            _targetView.SetOnKeyListener(this);

            UpdatePadLabel();
        }

        public bool OnKey(global::Android.Views.View v, Keycode keyCode, KeyEvent e)
        {
            // 押下か解放か
            bool isDown = e.Action == KeyEventActions.Down;

            if (keyCode == Keycode.ButtonA && isDown)
            {
                // Aボタンで ON/OFF トグル
                IsControlByPad = !IsControlByPad;
                UpdatePadLabel();
                return true;
            }

            if (!IsControlByPad)
                return false;  // 無効時は無視

            // 押下イベントのみ扱う
            if (!isDown)
                return false;

            switch (keyCode)
            {
                // D-Pad → Pitch/Roll
                case Keycode.DpadUp:
                    ApplyPitch(+1);
                    return true;
                case Keycode.DpadDown:
                    ApplyPitch(-1);
                    return true;
                case Keycode.DpadLeft:
                    ApplyRoll(-1);
                    return true;
                case Keycode.DpadRight:
                    ApplyRoll(+1);
                    return true;

                // L1/R1 → Yaw
                case Keycode.ButtonL1:
                    ApplyYaw(-1);
                    return true;
                case Keycode.ButtonR1:
                    ApplyYaw(+1);
                    return true;
            }

            return false;
        }

        void ApplyRoll(int direction)
        {
            // 左右傾き：U5[1], U5[2], U5[3], U5[4]
            U5[1] += direction * ROLL_GAIN;
            U5[2] -= direction * ROLL_GAIN;
            U5[3] -= direction * ROLL_GAIN;
            U5[4] += direction * ROLL_GAIN;
            UpdatePadLabel();
        }

        void ApplyPitch(int direction)
        {
            // 前後傾き：U5[1], U5[2], U5[3], U5[4]
            U5[1] += direction * PITCH_GAIN;
            U5[2] += direction * PITCH_GAIN;
            U5[3] -= direction * PITCH_GAIN;
            U5[4] -= direction * PITCH_GAIN;
            UpdatePadLabel();
        }

        void ApplyYaw(int direction)
        {
            // ヨー制御：Throttleは変えず、サーボだけ微調整
            U5[0] += direction * YAW_GAIN;  // 例として Throttle で回す
            UpdatePadLabel();
        }

        void UpdatePadLabel()
        {
            // UI 更新はメインスレッドで
            MainThread.BeginInvokeOnMainThread(() =>
            {
            });
        }

        public void Dispose()
        {
            // ページ破棄時などにリスナー解除
            _targetView.SetOnKeyListener(null);
        }
    }
}

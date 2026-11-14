# coding: utf-8
"""Тест ограничителя true peak (ccs_common.limit_true_peak): без GPU, нужны numpy и scipy.

Запуск: python3 deploy/comfyui/audio/test_limiter.py
"""
import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "workers"))
import ccs_common  # noqa: E402

SR = 48000


def tone(amp, sec=2.0, hz=1000.0, channels=2):
    t = np.arange(int(SR * sec)) / SR
    return np.stack([amp * np.sin(2 * np.pi * hz * t)] * channels).astype(np.float32)


class LimiterTests(unittest.TestCase):
    def test_overload_is_limited_below_ceiling(self):
        x = tone(1.14)  # пик +1,1 dBFS, как у MOSS-SFX
        self.assertGreater(ccs_common.true_peak_db(x, SR), 0.5)
        y = ccs_common.limit_true_peak(x, SR, -1.0)
        self.assertLessEqual(ccs_common.true_peak_db(y, SR), -1.0 + 1e-3)
        self.assertEqual(y.shape, x.shape)
        self.assertEqual(y.dtype, np.float32)

    def test_quiet_audio_untouched(self):
        x = tone(0.3)
        y = ccs_common.limit_true_peak(x, SR, -1.0)
        np.testing.assert_array_equal(x, y)

    def test_transient_burst_does_not_squash_whole_clip(self):
        x = tone(0.3, sec=3.0)
        x[:, SR:SR + 200] = 1.6 * np.sign(np.sin(np.arange(200) * 0.3))  # короткий выброс
        y = ccs_common.limit_true_peak(x, SR, -1.0)
        self.assertLessEqual(ccs_common.true_peak_db(y, SR), -1.0 + 1e-3)
        # далёкий от выброса участок почти не тронут
        self.assertAlmostEqual(float(np.max(np.abs(y[:, :SR // 2]))), 0.3, delta=0.01)

    def test_mono_and_empty(self):
        x = tone(1.3, channels=1)[0]
        y = ccs_common.limit_true_peak(x, SR, -1.0)
        self.assertEqual(y.shape, x.shape)
        self.assertLessEqual(ccs_common.true_peak_db(y, SR), -1.0 + 1e-3)
        self.assertEqual(ccs_common.limit_true_peak(np.zeros((2, 0), np.float32), SR).shape, (2, 0))


if __name__ == "__main__":
    unittest.main()

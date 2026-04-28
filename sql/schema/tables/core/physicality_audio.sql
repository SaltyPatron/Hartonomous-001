-- Physicality types 3..10: waveform, fft_spectrum, stft_spectrogram,
-- pitch_contour, formant_trajectory, spectral_centroid, mfcc_frame, chromagram.
-- Mixed geometry shapes (POINTZM for spectral_centroid, LINESTRINGZM for
-- contours/trajectories, MULTILINESTRINGZM for spectrograms) — no single
-- partition CHECK; per-row geometry validated by PostGIS internals.
CREATE TABLE substrate.physicality_audio
    PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);

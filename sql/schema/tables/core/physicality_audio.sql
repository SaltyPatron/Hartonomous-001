-- Physicality types 3..10: waveform, fft_spectrum, stft_spectrogram,
-- pitch_contour, formant_trajectory, spectral_centroid, mfcc_frame, chromagram.
-- Mixed geometry shapes (POINT4D for spectral_centroid, LINESTRING4D for
-- contours/trajectories, multi-trajectory shapes) — no single
-- partition CHECK.
CREATE TABLE substrate.physicality_audio
    PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);

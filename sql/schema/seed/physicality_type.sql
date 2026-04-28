-- Physicality types: 13 rows, ids 1..13 must match partition declarations.
INSERT INTO substrate.physicality_type (code) VALUES
    ('s3_position'),
    ('hilbert_value'),
    ('waveform'),
    ('fft_spectrum'),
    ('stft_spectrogram'),
    ('pitch_contour'),
    ('formant_trajectory'),
    ('spectral_centroid'),
    ('mfcc_frame'),
    ('chromagram'),
    ('svd_spectrum'),
    ('weight_distribution'),
    ('contour');

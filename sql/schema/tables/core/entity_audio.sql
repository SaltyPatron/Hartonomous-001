-- Entity types 20..21: audio_recording, audio_chunk.
CREATE TABLE substrate.entity_audio
    PARTITION OF substrate.entity FOR VALUES IN (20, 21);

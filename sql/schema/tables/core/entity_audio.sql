-- Entity types 13, 14: audio_recording, audio_chunk.
CREATE TABLE substrate.entity_audio
    PARTITION OF substrate.entity FOR VALUES IN (13, 14);

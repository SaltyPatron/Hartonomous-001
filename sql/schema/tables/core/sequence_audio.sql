CREATE TABLE substrate.sequence_audio
    PARTITION OF substrate.sequence FOR VALUES IN (13, 14);

CREATE TABLE substrate.sequence_lemma
    PARTITION OF substrate.sequence FOR VALUES IN (5);

CREATE TABLE substrate.edge_member_model_concept_similarity
    PARTITION OF substrate.edge_member FOR VALUES IN (60);

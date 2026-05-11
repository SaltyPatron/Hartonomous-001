CREATE DOMAIN substrate.modality_code AS VARCHAR(32)
    CONSTRAINT modality_code_known CHECK (
        VALUE IN ('text', 'image', 'audio', 'video', 'model_weights')
    );
COMMENT ON DOMAIN substrate.modality_code IS
    'Finite provenance authority modality code.';

CREATE TABLE substrate.physicality_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.physicality_type IS
    'Geometry interpretation. What the GeometryZM value in substrate.physicality represents (s3_position, contour, weight_distribution, etc.).';

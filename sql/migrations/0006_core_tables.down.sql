-- 0006_core_tables.down.sql
-- Drop partitioned parents (cascade drops partitions). Reverse of up order.

DROP TABLE IF EXISTS substrate.edge_member;
DROP TABLE IF EXISTS substrate.sequence;
DROP TABLE IF EXISTS substrate.significance CASCADE;
DROP TABLE IF EXISTS substrate.physicality CASCADE;
DROP TABLE IF EXISTS substrate.edge CASCADE;
DROP TABLE IF EXISTS substrate.entity CASCADE;

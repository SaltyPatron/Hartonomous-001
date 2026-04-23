# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 4: Final image — adds entrypoint, init scripts, postgres tuning.
# Result image:  hartonomous-postgres:latest
# ==============================================================================

ARG IMG_NS=hartonomous

FROM ${IMG_NS}/pgext:dev

USER root

# Entrypoint: initdb on first boot, then exec postgres.
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# SQL init scripts (CREATE EXTENSION postgis / hartonomous, etc.)
COPY sql/init /docker-entrypoint-initdb.d
RUN chown -R postgres:postgres /docker-entrypoint-initdb.d

USER postgres
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["postgres"]

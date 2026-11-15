"""Store the pilot assigned to each fleet aircraft."""

import sqlalchemy as sa
from alembic import op


revision = "0003_fleet_assignment"
down_revision = "0002_cloud_compute"
branch_labels = None
depends_on = None


def upgrade():
    # The initial migration creates current ORM tables for a clean database.
    columns = sa.inspect(op.get_bind()).get_columns("vehicles")
    if not any(column["name"] == "assigned_pilot" for column in columns):
        op.add_column("vehicles", sa.Column("assigned_pilot", sa.String(128)))


def downgrade():
    raise RuntimeError("Pilot assignment data requires a reviewed backup before downgrade")

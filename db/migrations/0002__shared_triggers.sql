-- =====================================================================================
-- 0002 — Shared trigger helpers
-- Source: server_db_schema.md §0.2
-- =====================================================================================

-- Stamps updated_at on every UPDATE. §0 makes updated_at mandatory on all mutable tables,
-- so this is attached once per such table rather than maintained by application code —
-- Dapper writes hand-written SQL and would otherwise have to remember it on every path.
CREATE OR REPLACE FUNCTION public.set_updated_at() RETURNS trigger AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION public.set_updated_at() IS
  'BEFORE UPDATE trigger: maintains the updated_at audit column (server_db_schema §0.2).';

-- Attaches set_updated_at to one table. Wrapping the CREATE TRIGGER keeps each table
-- migration to a single readable line and keeps the trigger naming consistent.
CREATE OR REPLACE FUNCTION public.attach_set_updated_at(p_schema TEXT, p_table TEXT) RETURNS void AS $$
BEGIN
  IF to_regclass(format('%I.%I', p_schema, p_table)) IS NULL THEN
    RAISE EXCEPTION 'attach_set_updated_at: relation %.% does not exist', p_schema, p_table;
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
     WHERE table_schema = p_schema AND table_name = p_table AND column_name = 'updated_at')
  THEN
    RAISE EXCEPTION 'attach_set_updated_at: %.% has no updated_at column', p_schema, p_table;
  END IF;

  -- CREATE OR REPLACE TRIGGER (PG 14+) makes re-running a migration a no-op.
  EXECUTE format(
    'CREATE OR REPLACE TRIGGER trg_%s_updated BEFORE UPDATE ON %I.%I '
    'FOR EACH ROW EXECUTE FUNCTION public.set_updated_at()',
    p_table, p_schema, p_table);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION public.attach_set_updated_at(TEXT, TEXT) IS
  'Attaches the set_updated_at BEFORE UPDATE trigger to a table (server_db_schema §0.2).';

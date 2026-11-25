create table if not exists assumption_sets (
    id text primary key,
    created_utc timestamptz not null,
    assumptions jsonb not null
);

create table if not exists runs (
    run_key text primary key,
    engine_version text not null,
    created_utc timestamptz not null,
    scenario_name text not null,
    result jsonb not null
);

create table if not exists ledger (
    id bigint generated always as identity primary key,
    at timestamptz not null,
    actor text not null,
    action text not null,
    subject_id text,
    engine_version text,
    input_hash text,
    output_hash text,
    correlation_id text not null
);

create or replace function ledger_is_append_only() returns trigger as $$
begin
    raise exception 'ledger is append-only: % is not allowed', tg_op;
end;
$$ language plpgsql;

drop trigger if exists ledger_append_only on ledger;
create trigger ledger_append_only
    before update or delete or truncate on ledger
    for each statement execute function ledger_is_append_only();

create table if not exists jobs (
    id text primary key,
    idempotency_key text unique,
    request_hash text not null,
    request text not null,
    correlation_id text not null,
    created_utc timestamptz not null,
    case_count int not null,
    chunk_count int not null
);

create table if not exists job_chunks (
    id bigint generated always as identity primary key,
    job_id text not null references jobs (id),
    chunk_index int not null,
    first_case int not null,
    case_count int not null,
    state text not null default 'queued' check (state in ('queued', 'leased', 'done', 'dead')),
    attempts int not null default 0,
    lease_owner text,
    lease_until timestamptz,
    last_error text,
    unique (job_id, chunk_index)
);

create index if not exists job_chunks_open on job_chunks (id) where state in ('queued', 'leased');

create table if not exists job_results (
    job_id text not null references jobs (id),
    case_index int not null,
    run_key text,
    error text,
    primary key (job_id, case_index)
);

create table if not exists deal_resources (
    kind text not null,
    resource_id text not null,
    deal_id text not null,
    primary key (kind, resource_id, deal_id)
);

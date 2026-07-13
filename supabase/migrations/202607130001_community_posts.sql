-- DevezCode community board (phase 1)
-- Public clients can only use the RPC functions below. The password hash and
-- private post body are never exposed through direct table access.

create extension if not exists pgcrypto with schema extensions;

create table if not exists public.community_posts (
    id uuid primary key default gen_random_uuid(),
    category text not null,
    title text not null,
    body text not null,
    author_name text not null,
    password_hash text not null,
    is_private boolean not null default false,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint community_posts_category_check
        check (category in ('improvement', 'bug', 'other')),
    constraint community_posts_title_check
        check (char_length(title) between 2 and 100),
    constraint community_posts_body_check
        check (char_length(body) between 1 and 5000),
    constraint community_posts_author_name_check
        check (char_length(author_name) between 2 and 20)
);

create index if not exists community_posts_created_at_idx
    on public.community_posts (created_at desc);
create index if not exists community_posts_category_created_at_idx
    on public.community_posts (category, created_at desc);

alter table public.community_posts enable row level security;
revoke all on table public.community_posts from public, anon, authenticated;

create or replace function public.set_community_post_updated_at()
returns trigger
language plpgsql
set search_path = public, pg_temp
as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists community_posts_set_updated_at on public.community_posts;
create trigger community_posts_set_updated_at
before update on public.community_posts
for each row execute function public.set_community_post_updated_at();

create or replace function public.community_list_posts(
    p_category text default null,
    p_search text default null,
    p_limit integer default 50,
    p_offset integer default 0
)
returns table (
    post_id uuid,
    category text,
    title text,
    author_name text,
    is_private boolean,
    created_at timestamptz,
    updated_at timestamptz,
    preview text,
    total_count bigint
)
language sql
security definer
stable
set search_path = public, pg_temp
as $$
    select
        p.id,
        p.category,
        p.title,
        p.author_name,
        p.is_private,
        p.created_at,
        p.updated_at,
        case when p.is_private then null else left(p.body, 180) end,
        count(*) over()
    from public.community_posts p
    where (p_category is null or p_category = '' or p.category = p_category)
      and (
          p_search is null or btrim(p_search) = ''
          or p.title ilike '%' || btrim(p_search) || '%'
          or (not p.is_private and p.body ilike '%' || btrim(p_search) || '%')
      )
    order by p.created_at desc, p.id desc
    limit least(greatest(coalesce(p_limit, 50), 1), 100)
    offset greatest(coalesce(p_offset, 0), 0);
$$;

create or replace function public.community_get_post(
    p_id uuid,
    p_password text default null
)
returns table (
    post_id uuid,
    category text,
    title text,
    body text,
    author_name text,
    is_private boolean,
    created_at timestamptz,
    updated_at timestamptz
)
language plpgsql
security definer
stable
set search_path = public, extensions, pg_temp
as $$
declare
    v_post public.community_posts%rowtype;
begin
    select * into v_post
    from public.community_posts
    where id = p_id;

    if not found then
        raise exception using errcode = 'P0001', message = 'COMMUNITY_NOT_FOUND';
    end if;

    if v_post.is_private and (
        p_password is null
        or extensions.crypt(p_password, v_post.password_hash) <> v_post.password_hash
    ) then
        raise exception using errcode = 'P0001', message = 'COMMUNITY_INVALID_PASSWORD';
    end if;

    return query select
        v_post.id,
        v_post.category,
        v_post.title,
        v_post.body,
        v_post.author_name,
        v_post.is_private,
        v_post.created_at,
        v_post.updated_at;
end;
$$;

create or replace function public.community_verify_password(
    p_id uuid,
    p_password text
)
returns boolean
language sql
security definer
stable
set search_path = public, extensions, pg_temp
as $$
    select coalesce((
        select extensions.crypt(p_password, p.password_hash) = p.password_hash
        from public.community_posts p
        where p.id = p_id
    ), false);
$$;

create or replace function public.community_create_post(
    p_category text,
    p_title text,
    p_body text,
    p_author_name text,
    p_password text,
    p_is_private boolean default false
)
returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
    v_category text := lower(btrim(p_category));
    v_title text := btrim(p_title);
    v_body text := btrim(p_body);
    v_author_name text := btrim(p_author_name);
begin
    if v_category not in ('improvement', 'bug', 'other') then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_CATEGORY';
    end if;
    if char_length(v_title) not between 2 and 100 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_TITLE';
    end if;
    if char_length(v_body) not between 1 and 5000 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_BODY';
    end if;
    if char_length(v_author_name) not between 2 and 20 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_AUTHOR';
    end if;
    if p_password is null or char_length(p_password) not between 4 and 72 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_PASSWORD_LENGTH';
    end if;

    insert into public.community_posts (
        category, title, body, author_name, password_hash, is_private
    ) values (
        v_category,
        v_title,
        v_body,
        v_author_name,
        extensions.crypt(p_password, extensions.gen_salt('bf', 10)),
        coalesce(p_is_private, false)
    )
    returning id into v_id;

    return v_id;
end;
$$;

create or replace function public.community_update_post(
    p_id uuid,
    p_category text,
    p_title text,
    p_body text,
    p_author_name text,
    p_password text,
    p_is_private boolean default false
)
returns boolean
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_category text := lower(btrim(p_category));
    v_title text := btrim(p_title);
    v_body text := btrim(p_body);
    v_author_name text := btrim(p_author_name);
begin
    if not public.community_verify_password(p_id, p_password) then
        raise exception using errcode = 'P0001', message = 'COMMUNITY_INVALID_PASSWORD';
    end if;
    if v_category not in ('improvement', 'bug', 'other') then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_CATEGORY';
    end if;
    if char_length(v_title) not between 2 and 100 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_TITLE';
    end if;
    if char_length(v_body) not between 1 and 5000 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_BODY';
    end if;
    if char_length(v_author_name) not between 2 and 20 then
        raise exception using errcode = '22023', message = 'COMMUNITY_INVALID_AUTHOR';
    end if;

    update public.community_posts
    set category = v_category,
        title = v_title,
        body = v_body,
        author_name = v_author_name,
        is_private = coalesce(p_is_private, false)
    where id = p_id;

    return found;
end;
$$;

create or replace function public.community_delete_post(
    p_id uuid,
    p_password text
)
returns boolean
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if not public.community_verify_password(p_id, p_password) then
        raise exception using errcode = 'P0001', message = 'COMMUNITY_INVALID_PASSWORD';
    end if;

    delete from public.community_posts where id = p_id;
    return found;
end;
$$;

revoke all on function public.set_community_post_updated_at() from public;
revoke all on function public.community_list_posts(text, text, integer, integer) from public;
revoke all on function public.community_get_post(uuid, text) from public;
revoke all on function public.community_verify_password(uuid, text) from public;
revoke all on function public.community_create_post(text, text, text, text, text, boolean) from public;
revoke all on function public.community_update_post(uuid, text, text, text, text, text, boolean) from public;
revoke all on function public.community_delete_post(uuid, text) from public;

grant execute on function public.community_list_posts(text, text, integer, integer) to anon, authenticated;
grant execute on function public.community_get_post(uuid, text) to anon, authenticated;
grant execute on function public.community_verify_password(uuid, text) to anon, authenticated;
grant execute on function public.community_create_post(text, text, text, text, text, boolean) to anon, authenticated;
grant execute on function public.community_update_post(uuid, text, text, text, text, text, boolean) to anon, authenticated;
grant execute on function public.community_delete_post(uuid, text) to anon, authenticated;

comment on table public.community_posts is
    'DevezCode community posts. Access only through community_* RPC functions.';

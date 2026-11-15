import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { cn } from "@/lib/utils";

export function SuggestField({
  label,
  value,
  onChange,
  onCommit,
  options,
  note,
  loading,
  empty,
  onOpen,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onCommit?: (value: string) => void;
  options: readonly string[];
  note: string;
  loading: boolean;
  empty: string;
  onOpen: () => void;
}) {
  const id = useId();
  const listId = useId();
  const [open, setOpen] = useState(false);
  const [filtering, setFiltering] = useState(false);
  const [active, setActive] = useState(0);
  const [place, setPlace] = useState({ top: 0, left: 0, width: 0, maxHeight: 160 });
  const openRef = useRef(false);
  const moved = useRef(false);
  const choosing = useRef(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const activeRef = useRef<HTMLButtonElement>(null);

  const shown = useMemo(() => {
    if (!filtering) return options;
    const query = value.trim().toLowerCase();
    if (!query) return options;
    return options.filter((name) => name.toLowerCase().includes(query));
  }, [filtering, options, value]);
  const shownKey = shown.join("\n");

  useEffect(() => {
    if (!open) return;
    if (moved.current) {
      setActive((index) => (shown.length === 0 ? 0 : Math.min(index, shown.length - 1)));
      return;
    }
    const match = shown.findIndex((name) => name === value.trim());
    setActive(match >= 0 ? match : 0);
  }, [open, shown, shownKey, value]);

  useLayoutEffect(() => {
    if (!open) return;
    function placeMenu() {
      const box = inputRef.current?.getBoundingClientRect();
      if (!box) return;
      const margin = 4;
      const below = window.innerHeight - box.bottom - margin;
      const above = box.top - margin;
      const openUp = below < 120 && above > below;
      const maxHeight = Math.max(80, Math.min(160, openUp ? above : below));
      const top = openUp ? Math.max(margin, box.top - margin - maxHeight) : box.bottom + margin;
      setPlace((current) =>
        current.top === top && current.left === box.left && current.width === box.width && current.maxHeight === maxHeight
          ? current
          : { top, left: box.left, width: box.width, maxHeight },
      );
    }
    placeMenu();
    window.addEventListener("resize", placeMenu);
    window.addEventListener("scroll", placeMenu, true);
    return () => {
      window.removeEventListener("resize", placeMenu);
      window.removeEventListener("scroll", placeMenu, true);
    };
  }, [open, shownKey]);

  useEffect(() => {
    const list = listRef.current;
    const item = activeRef.current;
    if (!list || !item) return;
    const top = item.offsetTop;
    const bottom = top + item.offsetHeight;
    if (top < list.scrollTop) list.scrollTop = top;
    else if (bottom > list.scrollTop + list.clientHeight) list.scrollTop = bottom - list.clientHeight;
  }, [active, open, shownKey]);

  function setMenu(next: boolean) {
    openRef.current = next;
    setOpen(next);
  }

  function openMenu() {
    const already = openRef.current;
    moved.current = false;
    setFiltering(false);
    setMenu(true);
    if (!already) onOpen();
  }

  function closeMenu() {
    setMenu(false);
  }

  function choose(name: string) {
    onChange(name);
    onCommit?.(name);
    closeMenu();
  }

  function move(delta: number) {
    if (shown.length === 0) return;
    moved.current = true;
    setActive((index) => (index + delta + shown.length) % shown.length);
  }

  const showChoices = shown.length > 0;
  const showStatus = !filtering && !showChoices && (loading || note !== "" || options.length === 0);
  const menu = open && (showChoices || showStatus);

  return (
    <div className="relative grid gap-1">
      <Label htmlFor={id} className="text-[11px] text-muted-foreground">{label}</Label>
      <Input
        id={id}
        ref={inputRef}
        className="h-7 text-[12px]"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={open}
        aria-controls={listId}
        autoComplete="off"
        autoCorrect="off"
        spellCheck={false}
        value={value}
        onChange={(event) => {
          moved.current = false;
          setFiltering(true);
          const already = openRef.current;
          setMenu(true);
          onChange(event.target.value);
          if (!already) onOpen();
        }}
        onFocus={openMenu}
        onClick={openMenu}
        onBlur={() => {
          closeMenu();
          if (choosing.current) {
            choosing.current = false;
            return;
          }
          onCommit?.(value);
        }}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown") {
            event.preventDefault();
            if (!openRef.current) openMenu();
            else move(1);
          } else if (event.key === "ArrowUp") {
            event.preventDefault();
            if (openRef.current) move(-1);
          } else if (event.key === "Enter" && openRef.current && shown[active]) {
            event.preventDefault();
            choose(shown[active]);
          } else if (event.key === "Escape") {
            closeMenu();
          }
        }}
      />
      {menu ? createPortal(
        <ul
          id={listId}
          ref={listRef}
          role="listbox"
          style={{ top: place.top, left: place.left, width: place.width, maxHeight: place.maxHeight }}
          className="fixed z-30 overflow-auto rounded-md border bg-card p-1 shadow-md"
          onMouseDown={(event) => event.preventDefault()}
        >
          {showChoices ? shown.map((name, index) => (
            <li key={name} role="presentation">
              <button
                type="button"
                role="option"
                aria-selected={index === active}
                ref={index === active ? activeRef : undefined}
                className={cn(
                  "flex w-full rounded-sm px-2 py-1 text-left text-[12px]",
                  index === active ? "bg-accent text-accent-foreground" : "hover:bg-accent hover:text-accent-foreground",
                  name === value.trim() && "font-medium",
                )}
                onMouseEnter={() => {
                  moved.current = true;
                  setActive(index);
                }}
                onMouseDown={(event) => {
                  event.preventDefault();
                  choosing.current = true;
                  window.setTimeout(() => {
                    choosing.current = false;
                  }, 0);
                }}
                onClick={() => choose(name)}
              >
                <span className="truncate">{name}</span>
              </button>
            </li>
          )) : (
            <li className={cn("px-2 py-1 text-sm", note ? "text-destructive" : "text-muted-foreground")}>
              {loading ? "Loading…" : note || empty}
            </li>
          )}
        </ul>,
        document.body,
      ) : null}
    </div>
  );
}

export function Field({
  label,
  value,
  onChange,
  onBlur,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
}) {
  const id = useId();
  return (
    <div className="grid gap-1">
      <Label htmlFor={id} className="text-[11px] text-muted-foreground">{label}</Label>
      <Input id={id} className="h-7 text-[12px]" value={value} onChange={(event) => onChange(event.target.value)} onBlur={onBlur} />
    </div>
  );
}

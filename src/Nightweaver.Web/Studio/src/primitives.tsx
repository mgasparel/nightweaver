import type { ReactNode } from "react";

const paths: Record<string, string> = {
  moon: "M20.9 13A9 9 0 0 1 11 3.1 9 9 0 1 0 20.9 13Z",
  overview: "M3 10 12 3l9 7v11h-6v-7H9v7H3Z",
  delivery: "M3 4h7v7H3z M14 4h7v7h-7z M3 15h7v6H3z M14 15h7v6h-7z",
  attention: "M12 3 2 21h20Z M12 9v5 M12 17v1",
  "catch-up": "M5 3h14v18H5Z M8 8h8 M8 12h8 M8 16h5",
  beads: "M12 3 3 8l9 5 9-5Z M3 12l9 5 9-5 M3 16l9 5 9-5",
  arrow: "M5 12h14 M13 6l6 6-6 6",
  plus: "M12 5v14 M5 12h14",
};

export function Icon({ name }: { name: string }) {
  return (
    <svg
      className="icon"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name] ?? paths.beads} />
    </svg>
  );
}

export function Status({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: "neutral" | "accent" | "warn";
}) {
  return <span className={`status ${tone}`}>{children}</span>;
}

export function Panel({
  title,
  accessory,
  children,
  className = "",
}: {
  title: string;
  accessory?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`panel ${className}`}>
      <header className="panel-heading">
        <h2>{title}</h2>
        {accessory}
      </header>
      <div className="panel-body">{children}</div>
    </section>
  );
}

export function Unavailable({
  title,
  children,
  href,
  action = "Open existing console",
}: {
  title: string;
  children: ReactNode;
  href?: string;
  action?: string;
}) {
  return (
    <div className="unavailable">
      <span className="eyebrow">Awaiting integration</span>
      <h3>{title}</h3>
      <p>{children}</p>
      {href && (
        <a className="button" href={href}>
          {action}
          <Icon name="arrow" />
        </a>
      )}
    </div>
  );
}

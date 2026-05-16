import { useState } from "react";
import "./Accordion.css";

interface AccordionProps {
  title: string;
  status?: string;
  children: React.ReactNode;
}

export default function Accordion({ title, status, children }: AccordionProps) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <div className={`accordion ${isOpen ? "accordion-open" : ""}`}>
      <button
        type="button"
        className="accordion-header"
        onClick={() => setIsOpen((prev) => !prev)}
        aria-expanded={isOpen}
      >
        <span className="accordion-title">{title}</span>
        {status && <span className="accordion-status">{status}</span>}
        <span className="accordion-chevron" />
      </button>
      <div className="accordion-body">
        <div className="accordion-body-inner">{children}</div>
      </div>
    </div>
  );
}

import { createRoot } from "react-dom/client";
import { Panel } from "./panel";
import "./styles/panel.css";

const root = document.getElementById("root");
if (!root) throw new Error("Missing panel root.");
createRoot(root).render(<Panel />);

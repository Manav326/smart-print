import React from "react";
import ReactDOM from "react-dom/client";
import PhotoStudioOfflineApp from "./PhotoStudioOfflineApp";
import "./styles.css";

const root = document.getElementById("root");
if (!root) throw new Error("Photo Studio root element is missing.");

ReactDOM.createRoot(root).render(
  <React.StrictMode>
    <PhotoStudioOfflineApp />
  </React.StrictMode>
);

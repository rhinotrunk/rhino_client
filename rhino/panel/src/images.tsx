import { useCallback, useState } from "react";
import { X } from "lucide-react";
import type { PromptImageData } from "./protocol/events";

const MAX_IMAGES = 4;
const MAX_EDGE = 1600;
const MAX_BYTES = 5_000_000;

// Shrink to a JPEG so a screenshot stays a few hundred KB over the bridge and in the model's context.
async function toJpeg(file: File): Promise<PromptImageData | null> {
  const bitmap = await createImageBitmap(file).catch(() => null);
  if (!bitmap) return null;
  const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(bitmap.width * scale));
  canvas.height = Math.max(1, Math.round(bitmap.height * scale));
  const context = canvas.getContext("2d");
  if (!context) return null;
  context.fillStyle = "#fff";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  const url = canvas.toDataURL("image/jpeg", 0.88);
  const data = url.slice(url.indexOf(",") + 1);
  if (data.length > MAX_BYTES * 1.37) return null;
  return { name: file.name || "pasted-image", mediaType: "image/jpeg", data };
}

export function usePromptImages() {
  const [images, setImages] = useState<PromptImageData[]>([]);
  const [note, setNote] = useState("");
  const add = useCallback(async (files: File[]) => {
    const list = files.filter((file) => file.type.startsWith("image/"));
    if (list.length === 0) return;
    const converted: PromptImageData[] = [];
    for (const file of list) {
      const image = await toJpeg(file);
      if (image) converted.push(image);
    }
    setNote(converted.length < list.length ? "Some images could not be read or were too large." : "");
    setImages((current) => {
      const next = [...current, ...converted].slice(0, MAX_IMAGES);
      if (current.length + converted.length > MAX_IMAGES) setNote(`Up to ${MAX_IMAGES} images per message.`);
      return next;
    });
  }, []);
  return {
    images, note,
    remove: (index: number) => setImages((current) => current.filter((_, i) => i !== index)),
    clear: () => { setImages([]); setNote(""); },
    // Spread onto a textarea: pasting or dropping an image attaches it instead of inserting text.
    handlers: {
      onPaste: (event: React.ClipboardEvent) => {
        const files = Array.from(event.clipboardData.files).filter((file) => file.type.startsWith("image/"));
        if (files.length === 0) return;
        event.preventDefault();
        void add(files);
      },
      onDrop: (event: React.DragEvent) => {
        const files = Array.from(event.dataTransfer.files).filter((file) => file.type.startsWith("image/"));
        if (files.length === 0) return;
        event.preventDefault();
        void add(files);
      },
      onDragOver: (event: React.DragEvent) => { if (Array.from(event.dataTransfer.types).includes("Files")) event.preventDefault(); },
    },
  };
}

export function ImageStrip({ images, note, onRemove }: { images: PromptImageData[]; note: string; onRemove: (index: number) => void }) {
  if (images.length === 0 && !note) return null;
  return (
    <div className="prompt-images">
      {images.map((image, index) => (
        <span key={index} className="prompt-image">
          <img src={`data:${image.mediaType};base64,${image.data}`} alt={image.name} />
          <button type="button" aria-label={`Remove ${image.name}`} onClick={() => onRemove(index)}><X size={11} /></button>
        </span>
      ))}
      {note ? <span className="prompt-images-note">{note}</span> : null}
    </div>
  );
}

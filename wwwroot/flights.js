const flightForm = document.querySelector("#flight-form");
const fromDate = document.querySelector("#from-date");
const toDate = document.querySelector("#to-date");
const departureInput = document.querySelector("#departure");
const destinationInput = document.querySelector("#destination");
const departureOptions = document.querySelector("#departure-options");
const destinationOptions = document.querySelector("#destination-options");
const flightButton = document.querySelector("#flight-button");
const flightResult = document.querySelector("#flight-result");
const flightStatusPill = document.querySelector("#flight-status-pill");

departureInput.addEventListener("input", debounce(() => loadLocations(departureInput, departureOptions), 350));
destinationInput.addEventListener("input", debounce(() => loadLocations(destinationInput, destinationOptions), 350));

flightForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  const payload = {
    fromDate: fromDate.value,
    toDate: toDate.value,
    departure: departureInput.value,
    destination: destinationInput.value
  };

  setStatus(flightStatusPill, "Scanning", "loading");
  flightButton.disabled = true;
  flightResult.className = "result empty";
  flightResult.textContent = "Resolving airports and checking fares...";

  try {
    const response = await fetch("/api/search-flights", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.detail || result.error || "Flight search failed.");
    }

    setStatus(flightStatusPill, result.liveFaresConfigured ? "Done" : "Config", result.liveFaresConfigured ? "done" : "error");
    flightResult.className = "result flight-results";
    flightResult.innerHTML = renderFlightResults(result);
  } catch (error) {
    setStatus(flightStatusPill, "Error", "error");
    flightResult.className = "result";
    flightResult.textContent = error.message;
  } finally {
    flightButton.disabled = false;
  }
});

async function loadLocations(input, datalist) {
  const query = input.value.trim();
  if (query.length < 2) {
    datalist.innerHTML = "";
    return;
  }

  const response = await fetch(`/api/flight-locations?query=${encodeURIComponent(query)}`);
  if (!response.ok) {
    return;
  }

  const locations = await response.json();
  datalist.innerHTML = "";

  for (const location of locations) {
    const option = document.createElement("option");
    option.value = location.label || `${location.city} - ${location.iata}`;
    datalist.append(option);
  }
}

function renderFlightResults(result) {
  const route = `${escapeHtml(result.departure.label)} to ${escapeHtml(result.destination.label)}`;
  const summary = `<h3>Summary</h3><p>${escapeHtml(result.summary)}</p>`;
  const routeMarkup = `<h3>Route</h3><p>${route}<br>${escapeHtml(result.fromDate)} to ${escapeHtml(result.toDate)}</p>`;
  const insight = renderPriceInsight(result.priceInsight);
  const flights = renderFlights(result.flights || []);
  const fallback = `<p><a href="${escapeHtml(result.fallbackSearchUrl)}" target="_blank" rel="noreferrer">Open this search in Google Flights</a></p>`;

  return routeMarkup + summary + insight + flights + fallback;
}

function renderPriceInsight(priceInsight) {
  if (!priceInsight) {
    return "";
  }

  const parts = [];
  if (priceInsight.lowestPrice) {
    parts.push(`Lowest seen: $${priceInsight.lowestPrice}`);
  }
  if (priceInsight.priceLevel) {
    parts.push(`Price level: ${priceInsight.priceLevel}`);
  }
  if (priceInsight.typicalLow && priceInsight.typicalHigh) {
    parts.push(`Typical range: $${priceInsight.typicalLow}-$${priceInsight.typicalHigh}`);
  }

  return parts.length ? `<h3>Price Insight</h3><p>${escapeHtml(parts.join(" | "))}</p>` : "";
}

function renderFlights(flights) {
  if (!flights.length) {
    return "<h3>Flight Options</h3><p>No priced live flight options are available yet.</p>";
  }

  const items = flights.map((flight) => {
    const duration = flight.totalDuration ? `${Math.floor(flight.totalDuration / 60)}h ${flight.totalDuration % 60}m` : "Duration unavailable";
    return `
      <li>
        <strong>${flight.price ? `$${flight.price}` : "Price unavailable"}</strong>
        <span>${escapeHtml(flight.airlines || "Airline unavailable")}</span>
        <span>${escapeHtml(flight.departureAirport)} ${escapeHtml(flight.departureTime)} -> ${escapeHtml(flight.arrivalAirport)} ${escapeHtml(flight.arrivalTime)}</span>
        <span>${duration}, ${flight.stops === 0 ? "nonstop" : `${flight.stops} stop${flight.stops > 1 ? "s" : ""}`}</span>
      </li>
    `;
  }).join("");

  return `<h3>Flight Options</h3><ol class="flight-list">${items}</ol>`;
}

function debounce(callback, delay) {
  let timeoutId;
  return (...args) => {
    clearTimeout(timeoutId);
    timeoutId = setTimeout(() => callback(...args), delay);
  };
}
